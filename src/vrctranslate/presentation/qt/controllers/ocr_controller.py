from __future__ import annotations

import logging
from uuid import uuid4

from PySide6.QtCore import QObject, QTimer, Signal
from PySide6.QtWidgets import QMessageBox

from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.ocr_translation_scheduler import (
    OcrTranslationOutcome,
    OcrTranslationScheduler,
)
from vrctranslate.application.use_cases.process_ocr_frame import ProcessOcrFrame
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.ocr import CaptureRegion, OcrText
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.ocr.rapidocr_engine import RapidOcrEngine
from vrctranslate.presentation.qt.controllers.ocr import (
    OcrCaptureSession,
    OcrTargetController,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow


class OcrController(QObject):
    """Coordinate target selection, capture session, translation and presentation."""

    tray_state_changed = Signal(str)
    capture_preview_ready = Signal(object, str)
    _scheduler_outcome = Signal(object)

    def __init__(
        self,
        page: OcrPage,
        overlay: OcrOverlayWindow,
        capture: FrameCapture,
        processor: ProcessOcrFrame,
        ocr_engine: RapidOcrEngine,
        translate_text: TranslateText,
        settings: ManageSettings,
        logger: logging.Logger,
        i18n: I18nManager | None = None,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._overlay = overlay
        self._capture = capture
        self._ocr_engine = ocr_engine
        self._settings = settings
        self._logger = logger
        self._i18n = i18n
        self._shutting_down = False
        self._session_failed = False
        self._ocr_active = False
        self._main_hidden_for_capture = False
        self._scheduler = OcrTranslationScheduler(
            translate_text, self._scheduler_outcome.emit
        )
        self._scheduler_outcome.connect(self._translation_completed)
        self._session = OcrCaptureSession(capture, processor, self)
        self._target = OcrTargetController(
            page,
            capture,
            settings,
            lambda: self._session.is_running,
            logger,
            self._i18n,
            self,
        )
        self._target.capture_preview_ready.connect(self.capture_preview_ready)
        self._geometry_timer = QTimer(self)
        self._geometry_timer.setSingleShot(True)
        self._geometry_timer.setInterval(400)
        self._geometry_timer.timeout.connect(self._save_overlay_geometry)

        page.refresh_requested.connect(self.refresh_windows)
        page.region_selection_requested.connect(self.select_region)
        page.toggle_requested.connect(self.toggle)
        overlay.geometry_changed.connect(lambda *_: self._geometry_timer.start())
        overlay.capture_exclusion_failed.connect(self._capture_exclusion_warning)
        self._session.status_changed.connect(page.set_status)
        self._session.texts_ready.connect(self._texts_ready)
        self._session.failed.connect(self._failed)
        self._session.finished.connect(self._finished)
        page.set_region(settings.current.ocr)
        self.apply_settings(settings.current)
        self.refresh_windows()

    def _tr(self, key: str, **kwargs: object) -> str:
        if self._i18n is not None:
            return self._i18n.tr(key, **kwargs)
        return key

    def apply_settings(self, settings: object) -> None:
        if hasattr(settings, "ui"):
            self._overlay.apply_settings(settings.ui)
        if hasattr(settings, "ocr"):
            self._capture.set_mode(settings.ocr.capture_backend)
        if hasattr(settings, "translation"):
            self._ocr_engine.set_source_language(
                settings.translation.ocr_route.source_language
            )
        self._update_capture_status()

    def _update_capture_status(self) -> None:
        try:
            meaning = self._tr(
                "capture.status_window"
                if self._capture.semantics == "window_content"
                else "capture.status_screen"
            )
            self._page.set_status(
                self._tr("capture.prefix", backend=self._capture.backend_name) + meaning
            )
        except VrcTranslateError as exc:
            self._page.set_status(exc.user_message)

    def refresh_windows(self) -> None:
        self._target.refresh_windows()
        if self._page.window_combo.count():
            self._update_capture_status()

    def select_region(self) -> None:
        self._target.select_region()

    def toggle(self) -> None:
        if self._session.is_running:
            self._page.set_status(self._tr("ctrl.ocr_stopping"))
            self._page.set_stopping()
            self._scheduler.stop()
            self._ocr_active = False
            self._overlay.hide()
            self._session.stop()
            return
        window = self._target.selected_window()
        if window is None:
            QMessageBox.warning(
                self._page,
                self._tr("ctrl.ocr_no_window"),
                self._tr("ctrl.ocr_no_window_msg"),
            )
            return
        settings = self._settings.current.ocr
        if settings.region_width <= 0 or settings.region_height <= 0:
            QMessageBox.warning(
                self._page,
                self._tr("ctrl.ocr_no_region"),
                self._tr("ctrl.ocr_no_region_msg"),
            )
            return
        region = CaptureRegion(
            settings.region_x,
            settings.region_y,
            settings.region_width,
            settings.region_height,
        )
        self._capture.set_mode(settings.capture_backend)
        try:
            screen_capture = self._capture.uses_screen_coordinates
        except VrcTranslateError as exc:
            self._page.set_status(exc.user_message)
            self.tray_state_changed.emit("error")
            return
        self._settings.current.translation.ensure_routes()
        self._scheduler.start(self._settings.current.translation)
        self._overlay.clear()
        self._ocr_active = True
        self._session_failed = False
        self._page.set_running(True)
        self.tray_state_changed.emit("ocr")
        if screen_capture:
            self._main_hidden_for_capture = True
            self._page.window().hide()
            self._page.set_status(self._tr("ctrl.ocr_mss_hint"))
        self._session.start(window.hwnd, region, settings, delayed=screen_capture)

    def test_capture(self, mode: str = "auto") -> None:
        self._target.test_capture(mode)

    def _texts_ready(self, items: list[OcrText]) -> None:
        route = self._settings.current.translation.ocr_route
        requests: list[TranslationRequest] = []
        for item in items:
            request_id = uuid4().hex
            self._page.add_recognition(request_id, item.text, item.confidence)
            requests.append(
                TranslationRequest(
                    request_id,
                    item.text,
                    route.source_language,
                    route.target_language,
                    "ocr",
                )
            )
        accepted = self._scheduler.submit_many(requests)
        for request in requests:
            if request.request_id not in accepted:
                self._page.set_translation(
                    request.request_id, self._tr("page.ocr.dropped")
                )
                self._logger.info("ocr_translation_dropped reason=queue_full")

    def _translation_completed(self, value: object) -> None:
        if not isinstance(value, OcrTranslationOutcome) or not self._ocr_active:
            return
        if value.result is not None:
            translated = value.result.translated
            original = value.result.original
            self._page.set_translation(value.request_id, translated)
            self._overlay.add_translation(original, translated)
            if not self._overlay.isVisible():
                self._overlay.show()
            return
        error = value.error
        self._logger.warning(
            "ocr_translation_failed category=%s",
            getattr(error, "category", "unexpected"),
        )
        message = (
            error.user_message
            if isinstance(error, VrcTranslateError)
            else self._tr("page.ocr.translation_failed", message=str(error))
        )
        self._page.set_translation(value.request_id, message)

    def _failed(self, message: str) -> None:
        self._session_failed = True
        self._ocr_active = False
        self._scheduler.stop()
        self._overlay.hide()
        self._page.set_status(message)
        self._logger.warning("ocr_failed")
        self.tray_state_changed.emit("error")
        self._restore_after_screen_capture()

    def _finished(self) -> None:
        self._ocr_active = False
        self._scheduler.stop()
        self._overlay.hide()
        self._page.set_running(False)
        self.tray_state_changed.emit("error" if self._session_failed else "normal")
        self._restore_after_screen_capture()

    def _restore_after_screen_capture(self) -> None:
        if self._main_hidden_for_capture and not self._shutting_down:
            self._main_hidden_for_capture = False
            self._target.restore_main_window()

    def _capture_exclusion_warning(self) -> None:
        self._page.set_status(self._tr("ctrl.ocr_exclusion_failed"))
        self._logger.warning("ocr_overlay_capture_exclusion_unavailable")

    def _save_overlay_geometry(self) -> None:
        ui = self._settings.current.ui
        ui.ocr_overlay_x = self._overlay.x()
        ui.ocr_overlay_y = self._overlay.y()
        ui.ocr_overlay_width = self._overlay.width()
        ui.ocr_overlay_height = self._overlay.height()
        self._settings.save(self._settings.current)

    def shutdown(self, timeout_ms: int = 10_000) -> bool:
        self._shutting_down = True
        self._ocr_active = False
        self._scheduler.shutdown()
        self._geometry_timer.stop()
        self._overlay.close_permanently()
        self._target.shutdown()
        return self._session.shutdown(timeout_ms)
