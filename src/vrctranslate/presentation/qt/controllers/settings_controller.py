from __future__ import annotations

import logging
from collections.abc import Callable

from PySide6.QtCore import QObject, QThreadPool, QUrl, Signal
from PySide6.QtGui import QDesktopServices

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.ocr_models import OcrModelManagement
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.presentation.qt.controllers.settings import (
    TranslationProfileTester,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker


class SettingsController(QObject):
    """Persist settings and coordinate translation-service diagnostics."""

    settings_changed = Signal(object)
    status_bar_message = Signal(str, int)

    def __init__(
        self,
        page: SettingsPage,
        settings: ManageSettings,
        translate_text: TranslateText,
        clear_logs: Callable[[], None],
        logger: logging.Logger,
        parent: QObject | None = None,
        i18n: I18nManager | None = None,
        ocr_models: OcrModelManagement | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._settings = settings
        self._clear_logs = clear_logs
        self._logger = logger
        self._i18n = i18n
        self._ocr_models = ocr_models
        self._model_workers: set[TaskWorker] = set()
        self._translation_tester = TranslationProfileTester(
            page,
            translate_text,
            logger,
            i18n,
            self,
        )
        page.save_requested.connect(self._save)
        page.test_translation_requested.connect(self._translation_tester.run)
        page.clear_logs_requested.connect(self._clear_log_files)
        page.open_path_requested.connect(self._open_path)
        page.discard_requested.connect(self._discard_changes)
        page.ocr_model_install_requested.connect(self._install_ocr_model)
        page.ocr_model_remove_requested.connect(self._remove_ocr_model)
        self._load_page()
        self._refresh_ocr_models()

    def _load_page(self) -> None:
        self._page.load_settings(
            self._settings.current,
            self._settings.location,
        )

    def _discard_changes(self) -> None:
        self._load_page()
        self.settings_changed.emit(self._settings.current)

    def _save(self) -> None:
        try:
            updated: AppSettings = self._page.collect_settings(
                self._settings.current
            )
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
        saved_message = (
            self._i18n.tr("ctrl.settings.saved", path=self._settings.location)
            if self._i18n
            else f"设置已保存：{self._settings.location}"
        )
        self.status_bar_message.emit(saved_message, 5000)
        self._logger.info("settings_saved")

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

    def _refresh_ocr_models(self) -> None:
        if self._ocr_models is None:
            return
        for status in self._ocr_models.statuses():
            self._page.set_ocr_model_status(
                status.language,
                status.installed,
                status.version,
                status.installed_size,
            )

    def _install_ocr_model(self, language: str) -> None:
        if self._ocr_models is None:
            return
        current = self._ocr_models.status(language)
        self._page.set_ocr_model_status(
            language,
            current.installed,
            current.version,
            current.installed_size,
            busy=True,
        )
        worker = TaskWorker(lambda: self._ocr_models.install(language))
        self._model_workers.add(worker)
        worker.signals.succeeded.connect(lambda _value: self._model_task_finished())
        worker.signals.failed.connect(
            lambda error, value=language: self._model_task_failed(value, error)
        )
        worker.signals.finished.connect(lambda: self._model_workers.discard(worker))
        QThreadPool.globalInstance().start(worker)

    def _remove_ocr_model(self, language: str) -> None:
        if self._ocr_models is None:
            return
        try:
            self._ocr_models.remove(language)
        except OSError as exc:
            self._model_task_failed(language, exc)
            return
        self._model_task_finished()

    def _model_task_finished(self) -> None:
        self._refresh_ocr_models()
        self.settings_changed.emit(self._settings.current)
        message = (
            self._i18n.tr("ocr_models.ready")
            if self._i18n is not None
            else "OCR 模型状态已更新"
        )
        self.status_bar_message.emit(message, 5000)

    def _model_task_failed(self, language: str, error: object) -> None:
        if self._ocr_models is None:
            return
        status = self._ocr_models.status(language)
        self._page.set_ocr_model_status(
            language,
            status.installed,
            status.version,
            status.installed_size,
            error=type(error).__name__,
        )
        self._logger.warning(
            "ocr_model_operation_failed language=%s error=%s",
            language,
            type(error).__name__,
        )
