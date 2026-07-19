from __future__ import annotations

import logging
from collections.abc import Callable
from pathlib import Path

from PySide6.QtCore import QObject, QThreadPool, QUrl, Signal
from PySide6.QtGui import QDesktopServices

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.ocr_models import OcrModelManagement
from vrctranslate.application.ports.speech_recognizer import SpeechRecognizer
from vrctranslate.application.ports.glossary_repository import GlossaryRepository
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.application.use_cases.translate_visual_frame import TranslateVisualFrame
from vrctranslate.domain.speech import (
    SpeechProfileValidationResult,
    SpeechRecognitionError,
)
from vrctranslate.presentation.qt.controllers.settings import (
    TranslationProfileTester,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.workers.ocr_model_install_worker import (
    OcrModelInstallWorker,
)
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
        glossary_repository: GlossaryRepository | None = None,
        translate_visual: TranslateVisualFrame | None = None,
        speech_validator: SpeechRecognizer | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._settings = settings
        self._clear_logs = clear_logs
        self._logger = logger
        self._i18n = i18n
        self._ocr_models = ocr_models
        self._glossary_repository = glossary_repository
        self._model_workers: dict[str, OcrModelInstallWorker] = {}
        self._speech_validator = speech_validator
        self._speech_worker: TaskWorker | None = None
        self._translation_tester = TranslationProfileTester(
            page,
            translate_text,
            logger,
            i18n,
            self,
            translate_visual,
        )
        page.save_requested.connect(self._save)
        page.test_translation_requested.connect(self._translation_tester.run)
        page.clear_logs_requested.connect(self._clear_log_files)
        page.open_path_requested.connect(self._open_path)
        page.discard_requested.connect(self._discard_changes)
        page.ocr_model_install_requested.connect(self._install_ocr_model)
        page.ocr_model_remove_requested.connect(self._remove_ocr_model)
        page.ocr_model_cancel_requested.connect(self._cancel_ocr_model)
        page.glossary_import_requested.connect(self._import_glossary)
        page.glossary_export_requested.connect(self._export_glossary)
        page.speech_profile_test_requested.connect(self._validate_speech_profile)
        self._load_page()
        self._refresh_ocr_models()

    def _validate_speech_profile(self) -> None:
        if self._speech_validator is None or self._speech_worker is not None:
            return
        profile = self._page.selected_speech_profile()
        self._page.set_speech_validation_busy(True)
        worker = TaskWorker(lambda: self._speech_validator.validate_profile(profile))
        self._speech_worker = worker
        worker.signals.succeeded.connect(
            lambda result, profile_id=profile.id: self._speech_validation_succeeded(
                profile_id, result
            )
        )
        worker.signals.failed.connect(
            lambda error, profile_id=profile.id: self._speech_validation_failed(
                profile_id, error
            )
        )
        worker.signals.finished.connect(
            lambda current=worker: self._speech_validation_finished(current)
        )
        QThreadPool.globalInstance().start(worker)

    def _speech_validation_succeeded(
        self,
        profile_id: str,
        result: object,
    ) -> None:
        if not isinstance(result, SpeechProfileValidationResult):
            self._speech_validation_failed(profile_id, RuntimeError("invalid result"))
            return
        self._page.set_speech_validation_result(
            profile_id,
            result.state,
            result.message,
        )

    def _speech_validation_failed(self, profile_id: str, error: object) -> None:
        message = (
            error.user_message
            if isinstance(error, SpeechRecognitionError)
            else self._i18n.tr("speech_profile.validation_unexpected")
            if self._i18n is not None
            else "实时语音连接验证失败"
        )
        self._page.set_speech_validation_result(profile_id, "failed", message)
        self._logger.warning(
            "speech_profile_validation_failed category=%s",
            getattr(error, "category", type(error).__name__),
        )

    def _speech_validation_finished(self, worker: TaskWorker) -> None:
        if self._speech_worker is worker:
            self._speech_worker = None
        self._page.set_speech_validation_busy(False)

    def _load_page(self) -> None:
        if self._glossary_repository is not None:
            self._page.set_glossary_entries(
                self._glossary_repository.builtin_entries(),
                self._glossary_repository.user_entries(),
            )
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
            if self._glossary_repository is not None:
                self._glossary_repository.save_user_entries(
                    self._page.user_glossary_entries()
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

    def _import_glossary(self, path: str) -> None:
        repository = self._glossary_repository
        if repository is None:
            return
        try:
            entries = repository.load_external(Path(path))
            self._page.set_user_glossary_entries(entries)
        except (OSError, ValueError) as exc:
            message = (
                self._i18n.tr("glossary.import_failed", error=str(exc))
                if self._i18n
                else f"术语导入失败：{exc}"
            )
            self.status_bar_message.emit(message, 6000)
            return
        message = (
            self._i18n.tr("glossary.imported", count=len(entries))
            if self._i18n
            else f"已导入 {len(entries)} 条术语，保存修改后生效"
        )
        self.status_bar_message.emit(message, 5000)

    def _export_glossary(self, path: str, entries: object) -> None:
        repository = self._glossary_repository
        if repository is None or not isinstance(entries, list):
            return
        try:
            repository.export_external(Path(path), entries)
        except (OSError, ValueError) as exc:
            message = (
                self._i18n.tr("glossary.export_failed", error=str(exc))
                if self._i18n
                else f"术语导出失败：{exc}"
            )
            self.status_bar_message.emit(message, 6000)
            return
        message = (
            self._i18n.tr("glossary.exported", count=len(entries))
            if self._i18n
            else f"已导出 {len(entries)} 条用户术语"
        )
        self.status_bar_message.emit(message, 5000)

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
                download_size=status.required_download_size,
                exclusive_size=status.exclusive_size,
            )
        storage = self._ocr_models.storage()
        self._page.set_ocr_model_storage(storage.shared_size, storage.total_size)

    def _install_ocr_model(self, language: str) -> None:
        if self._ocr_models is None:
            return
        if language in self._model_workers:
            return
        current = self._ocr_models.status(language)
        self._page.set_ocr_model_status(
            language,
            current.installed,
            current.version,
            current.installed_size,
            download_size=current.required_download_size or current.download_size,
            exclusive_size=current.exclusive_size,
            busy=True,
        )
        worker = OcrModelInstallWorker(
            lambda progress: self._ocr_models.install(language, progress)
        )
        self._model_workers[language] = worker
        worker.signals.progress.connect(
            lambda completed, total, value=language: self._page.set_ocr_model_progress(
                value, completed, total
            )
        )
        worker.signals.succeeded.connect(
            lambda _value, value=language: self._model_task_finished(value)
        )
        worker.signals.failed.connect(
            lambda error, value=language: self._model_task_failed(value, error)
        )
        worker.signals.cancelled.connect(
            lambda value=language: self._model_task_cancelled(value)
        )
        worker.signals.finished.connect(
            lambda value=language: self._model_workers.pop(value, None)
        )
        QThreadPool.globalInstance().start(worker)

    def _cancel_ocr_model(self, language: str) -> None:
        worker = self._model_workers.get(language)
        if worker is not None:
            worker.cancel()

    def _remove_ocr_model(self, language: str) -> None:
        if self._ocr_models is None:
            return
        try:
            self._ocr_models.remove(language)
        except OSError as exc:
            self._model_task_failed(language, exc)
            return
        self._model_task_finished(language)

    def _model_task_finished(self, _language: str) -> None:
        self._refresh_ocr_models()
        self.settings_changed.emit(self._settings.current)
        message = (
            self._i18n.tr("ocr_models.ready")
            if self._i18n is not None
            else "OCR 模型状态已更新"
        )
        self.status_bar_message.emit(message, 5000)

    def _model_task_cancelled(self, _language: str) -> None:
        self._refresh_ocr_models()
        message = (
            self._i18n.tr("ocr_models.cancelled")
            if self._i18n is not None
            else "OCR 模型下载已取消"
        )
        self.status_bar_message.emit(message, 4000)

    def _model_task_failed(self, language: str, error: object) -> None:
        if self._ocr_models is None:
            return
        status = self._ocr_models.status(language)
        self._page.set_ocr_model_status(
            language,
            status.installed,
            status.version,
            status.installed_size,
            download_size=status.required_download_size or status.download_size,
            exclusive_size=status.exclusive_size,
            error=type(error).__name__,
        )
        self._logger.warning(
            "ocr_model_operation_failed language=%s error=%s",
            language,
            type(error).__name__,
        )
