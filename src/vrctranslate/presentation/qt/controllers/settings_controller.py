from __future__ import annotations

import logging
from collections.abc import Callable
from uuid import uuid4

from PySide6.QtCore import QObject, QThreadPool, QUrl, Signal
from PySide6.QtGui import QDesktopServices

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.local_models import (
    LocalModelManager,
    LocalTranslationModel,
)
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.manage_local_models import (
    LocalModelCatalog,
    ManageLocalModels,
)
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.translation import TranslationRequest, TranslationResult
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker


class _ProgressRelay(QObject):
    progress = Signal(int, int)


class SettingsController(QObject):
    settings_changed = Signal(object)
    status_bar_message = Signal(str, int)

    def __init__(
        self,
        page: SettingsPage,
        settings: ManageSettings,
        translate_text: TranslateText,
        model_manager: LocalModelManager,
        clear_logs: Callable[[], None],
        logger: logging.Logger,
        parent: QObject | None = None,
        i18n: I18nManager | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._settings = settings
        self._translate_text = translate_text
        self._models = model_manager
        self._model_catalog = ManageLocalModels(model_manager)
        self._clear_logs = clear_logs
        self._logger = logger
        self._i18n = i18n
        self._thread_pool = QThreadPool.globalInstance()
        self._available_models: list[LocalTranslationModel] = []
        self._automatic_index_refresh_started = False
        self._pending_installs: list[tuple[str, str, str]] = []
        page.save_requested.connect(self._save)
        page.test_translation_requested.connect(self._test_translation)
        page.argos_refresh_requested.connect(lambda: self._refresh_models(True))
        page.argos_catalog_requested.connect(lambda: self._refresh_models(False))
        page.argos_install_requested.connect(self._install_model)
        page.argos_pivot_install_requested.connect(self._install_pivot_models)
        page.argos_remove_requested.connect(self._remove_model)
        page.open_models_requested.connect(self._open_models)
        page.clear_logs_requested.connect(self._clear_log_files)
        page.open_path_requested.connect(self._open_path)
        page.discard_requested.connect(self._load_page)
        self._load_page()

    def _load_page(self) -> None:
        self._page.load_settings(
            self._settings.current,
            self._settings.location,
            self._models.component_available,
            self._models.model_directory,
        )
        if self._models.component_available and not self._automatic_index_refresh_started:
            self._automatic_index_refresh_started = True
            self._refresh_models(False)

    def _save(self) -> None:
        try:
            updated: AppSettings = self._page.collect_settings(self._settings.current)
            self._settings.save(updated)
        except ValueError as exc:
            message = (
                self._i18n.tr("ctrl.settings.save_failed", error=str(exc))
                if self._i18n
                else f"设置未保存：{exc}"
            )
            self.status_bar_message.emit(message, 6000)
            return
        except OSError:
            message = (
                self._i18n.tr("ctrl.settings.save_disk")
                if self._i18n
                else "设置保存失败：data 目录不可写"
            )
            self.status_bar_message.emit(message, 6000)
            return
        if self._i18n is not None:
            self._i18n.set_language(updated.ui.language)
        self._load_page()
        self.settings_changed.emit(updated)
        saved_msg = (
            self._i18n.tr("ctrl.settings.saved", path=self._settings.location)
            if self._i18n
            else f"设置已保存：{self._settings.location}"
        )
        self.status_bar_message.emit(saved_msg, 5000)
        self._logger.info("settings_saved")

    def _test_translation(self) -> None:
        profile = self._page.selected_profile()
        request = TranslationRequest(
            uuid4().hex,
            "Hello, world!",
            "en",
            "zh-CN",
            "self",
        )
        status = (
            self._i18n.tr("ctrl.settings.testing")
            if self._i18n
            else "正在使用固定短句测试…"
        )
        self._page.set_test_status(status)
        worker = TaskWorker(lambda: self._translate_text.execute(request, profile))
        worker.signals.succeeded.connect(self._test_succeeded)
        worker.signals.failed.connect(self._test_failed)
        self._thread_pool.start(worker)

    def _test_succeeded(self, value: object) -> None:
        if isinstance(value, TranslationResult):
            message = (
                self._i18n.tr("ctrl.settings.test_ok", text=value.translated)
                if self._i18n
                else f"测试成功：{value.translated}"
            )
            self._page.set_test_status(message)

    def _test_failed(self, error: object) -> None:
        if isinstance(error, VrcTranslateError):
            self._page.set_test_status(error.user_message, True)
        else:
            message = (
                self._i18n.tr("ctrl.settings.test_fail")
                if self._i18n
                else "测试失败，请查看运行日志"
            )
            self._page.set_test_status(message, True)
        self._logger.warning(
            "translation_test_failed category=%s",
            getattr(error, "category", "unexpected"),
        )

    def _refresh_models(self, online: bool) -> None:
        if not self._models.component_available:
            self._page.set_model_catalog(
                "component_missing", [], [], 0, "当前环境未安装 Argos Translate 组件。"
            )
            return
        self._page.set_model_catalog(
            "loading",
            [],
            self._available_models,
            0,
            "正在刷新官方模型索引…" if online else "正在读取便携模型索引…",
        )

        worker = TaskWorker(lambda: self._model_catalog.load_catalog(online))
        worker.signals.succeeded.connect(self._models_loaded)
        worker.signals.failed.connect(self._model_failed)
        self._thread_pool.start(worker)

    def _models_loaded(self, value: object) -> None:
        if not isinstance(value, LocalModelCatalog):
            return
        self._available_models = list(value.available)
        self._page.set_model_catalog(
            value.state,
            list(value.installed),
            list(value.available),
            value.disk_usage,
            value.message,
        )
        if value.state == "index_missing" and not self._automatic_index_refresh_started:
            self._automatic_index_refresh_started = True
            self._refresh_models(True)

    def _install_model(self, source: str, target: str, version: str) -> None:
        self._pending_installs = []
        self._run_install(
            source, target, version,
            f"正在安装 {source} → {target}；文件只写入软件 data 目录…",
        )

    def _install_pivot_models(self, models: list) -> None:
        self._pending_installs = [
            (str(s), str(t), str(v)) for s, t, v in models
        ]
        self._install_next_pending()

    def _install_next_pending(self) -> None:
        if not self._pending_installs:
            self._refresh_models(False)
            return
        source, target, version = self._pending_installs.pop(0)
        remaining = len(self._pending_installs)
        suffix = f"（剩余 {remaining} 个）" if remaining else ""
        self._run_install(
            source, target, version,
            f"正在安装 {source} → {target}（中转模型{suffix}）；文件只写入软件 data 目录…",
        )

    def _run_install(self, source: str, target: str, version: str, status: str) -> None:
        self._page.set_argos_status(status)
        relay = _ProgressRelay()
        relay.progress.connect(self._page.set_argos_progress)
        self._progress_relay = relay
        worker = TaskWorker(
            lambda: self._models.install(source, target, version, progress_callback=relay.progress.emit)
        )
        worker.signals.succeeded.connect(self._install_succeeded)
        worker.signals.failed.connect(self._model_failed)
        self._thread_pool.start(worker)

    def _install_succeeded(self, _: object) -> None:
        self._page.hide_argos_progress()
        if self._pending_installs:
            self._install_next_pending()
        else:
            self._refresh_models(False)

    def _remove_model(self, source: str, target: str) -> None:
        self._run_model_change(
            lambda: self._models.remove(source, target),
            f"正在删除 {source} → {target}…",
        )

    def _run_model_change(self, operation: Callable[[], None], status: str) -> None:
        self._page.set_argos_status(status)
        worker = TaskWorker(operation)
        worker.signals.succeeded.connect(lambda _: self._refresh_models(False))
        worker.signals.failed.connect(self._model_failed)
        self._thread_pool.start(worker)

    def _model_failed(self, error: object) -> None:
        self._page.hide_argos_progress()
        self._pending_installs = []
        message = (
            error.user_message
            if isinstance(error, VrcTranslateError)
            else "Argos 模型操作失败，请查看运行日志"
        )
        self._page.set_model_catalog(
            "error", [], self._available_models, 0, message
        )
        self._logger.warning(
            "argos_model_operation_failed category=%s",
            getattr(error, "category", "unexpected"),
        )

    def _open_models(self) -> None:
        QDesktopServices.openUrl(QUrl.fromLocalFile(self._models.model_directory))

    def _open_path(self, key: str) -> None:
        path = self._page.path_for(key)
        if path:
            QDesktopServices.openUrl(QUrl.fromLocalFile(path))

    def _clear_log_files(self) -> None:
        try:
            self._clear_logs()
        except OSError:
            message = (
                self._i18n.tr("ctrl.settings.logs_failed")
                if self._i18n
                else "日志清理失败，请关闭程序后手工删除"
            )
            self.status_bar_message.emit(message, 5000)
            return
        message = (
            self._i18n.tr("ctrl.settings.logs_cleared")
            if self._i18n
            else "运行日志已清空"
        )
        self.status_bar_message.emit(message, 4000)
