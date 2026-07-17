from __future__ import annotations

import logging
from copy import deepcopy
from uuid import uuid4

from PySide6.QtCore import QObject, QTimer, Signal
from PySide6.QtWidgets import QMessageBox

from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.ports.ocr_engine import OcrEngine
from vrctranslate.application.ports.window_capture import WindowActivator
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.ocr.translation_context import RecentOcrContext
from vrctranslate.application.use_cases.ocr_translation_scheduler import (
    OcrTranslationOutcome,
    OcrTranslationScheduler,
)
from vrctranslate.application.use_cases.process_ocr_frame import ProcessOcrFrame
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.ocr import CaptureRegion, OcrText, WindowInfo
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.presentation.qt.controllers.ocr import OcrCaptureSession, OcrTargetController
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.windows.ocr_orb import OcrOrbWindow
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow
from vrctranslate.presentation.qt.windows.ocr_region import OcrRegionWindow


class OcrController(QObject):
    """Coordinate OCR commands while windows and pages remain passive views."""

    tray_state_changed = Signal(str)
    capture_preview_ready = Signal(object, str)
    overlay_geometry_changed = Signal(int, int, int, int)
    _scheduler_outcome = Signal(object)

    def __init__(
        self,
        page: OcrPage,
        overlay: OcrOverlayWindow,
        region_window: OcrRegionWindow,
        orb_window: OcrOrbWindow,
        capture: FrameCapture,
        processor: ProcessOcrFrame,
        ocr_engine: OcrEngine,
        translate_text: TranslateText,
        settings: ManageSettings,
        windows_api: WindowActivator,
        logger: logging.Logger,
        i18n: I18nManager | None = None,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._overlay = overlay
        self._region_window = region_window
        self._orb = orb_window
        self._capture = capture
        self._ocr_engine = ocr_engine
        self._settings = settings
        self._windows_api = windows_api
        self._logger = logger
        self._i18n = i18n
        self._shutting_down = False
        self._session_failed = False
        self._ocr_active = False
        self._stopping = False
        self._single_capture_finished = False
        self._pending_start_mode: str | None = None
        self._region_interacting = False
        self._available_windows: list[WindowInfo] = []
        self._scheduler = OcrTranslationScheduler(translate_text, self._scheduler_outcome.emit)
        self._translation_context = RecentOcrContext()
        self._scheduler_outcome.connect(self._translation_completed)
        self._session = OcrCaptureSession(capture, processor, self)
        self._target = OcrTargetController(
            page,
            capture,
            settings,
            lambda: self._session.is_running,
            logger,
            windows_api,
            i18n,
            self,
        )
        self._target.capture_preview_ready.connect(self.capture_preview_ready)
        self._target.target_changed.connect(self._target_changed)
        self._target.region_selected.connect(self._region_selected)

        page.target_selected.connect(self._select_target_from_page)
        page.refresh_targets_requested.connect(self.refresh_target)
        page.ui_settings_changed.connect(self._preview_page_settings)
        page.overlay_preview_changed.connect(self.preview_overlay_style)
        page.overlay_show_requested.connect(self.show_overlay_preview)
        page.overlay_reset_requested.connect(self.reset_overlay_geometry)
        overlay.geometry_changed.connect(self._overlay_geometry_changed)
        overlay.capture_exclusion_failed.connect(self._capture_exclusion_warning)

        orb_window.toggle_requested.connect(self.toggle)
        orb_window.single_requested.connect(lambda: self.start_mode("single"))
        orb_window.continuous_requested.connect(lambda: self.start_mode("continuous"))
        orb_window.region_requested.connect(self.select_region)
        orb_window.region_visibility_requested.connect(region_window.toggle_visibility)
        orb_window.exit_requested.connect(self.exit_ocr_tools)
        orb_window.geometry_changed.connect(self._save_orb_geometry)

        region_window.mode_requested.connect(self.start_mode)
        region_window.close_requested.connect(self._close_region)
        region_window.region_changed.connect(self._region_moved)
        region_window.interaction_started.connect(self._region_interaction_started)
        region_window.interaction_finished.connect(self._region_interaction_finished)

        self._session.status_changed.connect(self._session_status)
        self._session.texts_ready.connect(self._texts_ready)
        self._session.failed.connect(self._failed)
        self._session.finished.connect(self._finished)

        self._target_timer = QTimer(self)
        self._target_timer.setInterval(500)
        self._target_timer.timeout.connect(self._sync_region_to_target)
        self._target_timer.start()
        self._ui_settings_timer = QTimer(self)
        self._ui_settings_timer.setSingleShot(True)
        self._ui_settings_timer.setInterval(350)
        self._ui_settings_timer.timeout.connect(self._save_page_settings)

        self.apply_settings(settings.current)
        self.refresh_target()
        self.show_orb()

    def _tr(self, key: str, **kwargs: object) -> str:
        return self._i18n.tr(key, **kwargs) if self._i18n is not None else key

    @property
    def has_unsaved_changes(self) -> bool:
        return self._page.has_unsaved_changes

    def apply_settings(self, settings: object) -> None:
        if not hasattr(settings, "ui"):
            return
        self._overlay.apply_settings(settings.ui)
        self._orb.apply_settings(settings.ui)
        self._page.load_settings(settings)
        if hasattr(settings, "ocr"):
            self._capture.set_mode(settings.ocr.capture_backend)
            self._region_window.set_mode(settings.ocr.recognition_mode)
        if hasattr(settings, "translation"):
            settings.translation.ensure_routes()
            route = settings.translation.ocr_route
            self._ocr_engine.set_source_language(route.source_language)
            profile = settings.translation.profile_for_purpose("ocr")
            self._page.set_runtime_summary(
                mode=settings.ocr.recognition_mode,
                language=f"{route.source_language} → {route.target_language}",
                profile=profile.name,
            )
        self._update_capture_status()

    def refresh_target(self) -> None:
        self._available_windows = self._target.refresh_windows()
        window = self._target.selected_window()
        self._page.set_target_windows(
            self._available_windows,
            window.hwnd if window is not None else None,
        )
        if window is None:
            return
        self._target_changed(window, save=False)

    def _select_target_from_page(self, hwnd: int) -> None:
        if self._session.is_running or self._ocr_active:
            self._stop_session()
            QTimer.singleShot(220, lambda: self._select_target_from_page(hwnd))
            return
        window = self._target.select_window(hwnd)
        if window is None:
            self.refresh_target()
            return
        ocr = self._settings.current.ocr
        ocr.region_x = ocr.region_y = ocr.region_width = ocr.region_height = 0
        ocr.window_title = window.title
        self._settings.save(self._settings.current)
        self._region_window.hide()
        self._page.set_status(self._tr("ctrl.ocr_target_selected"))

    def select_region(self) -> None:
        if self._session.is_running or self._ocr_active:
            self._stop_session()
            QTimer.singleShot(180, self._target.select_region)
            return
        self._target.select_region()

    def show_orb(self) -> None:
        self._orb.show_and_raise()

    def toggle(self) -> None:
        if self._session.is_running or self._ocr_active:
            self._stop_session()
            return
        self.start_mode(self._settings.current.ocr.recognition_mode)

    def start_mode(self, mode: str) -> None:
        mode = "single" if mode == "single" else "continuous"
        if self._session.is_running or self._ocr_active:
            self._pending_start_mode = mode
            self._stop_session()
            return
        self._set_mode(mode)
        self._start_session()

    def _start_session(self) -> None:
        window = self._target.selected_window()
        if window is None:
            QMessageBox.warning(
                self._page,
                self._tr("ctrl.ocr_no_window"),
                self._tr("ctrl.ocr_no_window_msg"),
            )
            self._orb.set_state("error")
            return
        settings = deepcopy(self._settings.current.ocr)
        if settings.region_width <= 0 or settings.region_height <= 0:
            self.select_region()
            return
        region = CaptureRegion(
            settings.region_x,
            settings.region_y,
            settings.region_width,
            settings.region_height,
        )
        self._region_window.set_target(window, region)
        self._region_window.set_mode(settings.recognition_mode)
        self._region_window.show()
        self._region_window.raise_()
        self._capture.set_mode(settings.capture_backend)
        try:
            self._capture.uses_screen_coordinates
        except VrcTranslateError as exc:
            self._set_error(exc.user_message)
            return
        self._windows_api.activate_window(window.hwnd)
        self._settings.current.translation.ensure_routes()
        self._scheduler.start(self._settings.current.translation)
        self._translation_context.clear()
        self._overlay.clear()
        self._ocr_active = True
        self._stopping = False
        self._single_capture_finished = False
        self._session_failed = False
        self._set_visual_state("running")
        self._page.set_status(
            self._tr(
                "ctrl.ocr_single_running"
                if settings.recognition_mode == "single"
                else "ctrl.ocr_continuous_running"
            )
        )
        self.tray_state_changed.emit("ocr")
        self._session.start(window.hwnd, region, settings)

    def _stop_session(self) -> None:
        self._stopping = True
        self._ocr_active = False
        self._single_capture_finished = False
        self._page.set_status(self._tr("ctrl.ocr_stopping"))
        self._scheduler.stop()
        self._translation_context.clear()
        if self._session.is_running:
            self._session.stop()
        else:
            self._finished()

    def _set_mode(self, mode: str) -> None:
        if self._settings.current.ocr.recognition_mode == mode:
            self._region_window.set_mode(mode)
            self._page.set_runtime_summary(mode=mode)
            return
        self._settings.current.ocr.recognition_mode = mode
        self._settings.save(self._settings.current)
        self._region_window.set_mode(mode)
        self._page.set_runtime_summary(mode=mode)

    def _target_changed(self, value: object, save: bool = True) -> None:
        if not isinstance(value, WindowInfo):
            return
        if save:
            self._settings.current.ocr.window_title = value.title
            self._settings.save(self._settings.current)
        if self._available_windows:
            self._page.set_target_windows(self._available_windows, value.hwnd)
        current = self._settings.current.ocr
        if current.region_width > 0 and current.region_height > 0:
            region = CaptureRegion(
                current.region_x,
                current.region_y,
                min(current.region_width, value.width),
                min(current.region_height, value.height),
            )
            self._region_window.set_target(value, region)

    def _region_selected(self, value: object) -> None:
        if not isinstance(value, CaptureRegion):
            return
        window = self._target.selected_window()
        if window is None:
            return
        ocr = self._settings.current.ocr
        ocr.region_x = value.x
        ocr.region_y = value.y
        ocr.region_width = value.width
        ocr.region_height = value.height
        ocr.window_title = window.title
        self._settings.save(self._settings.current)
        self._region_window.set_target(window, value)
        self._region_window.set_mode(ocr.recognition_mode)
        self._region_window.show()
        self._region_window.raise_()
        self._set_visual_state("idle")
        self._page.set_status(self._tr("ctrl.ocr.region_saved"))

    def _region_moved(self, value: object) -> None:
        if not isinstance(value, CaptureRegion):
            return
        if self._session.is_running or self._ocr_active:
            self._stop_session()
        ocr = self._settings.current.ocr
        ocr.region_x, ocr.region_y = value.x, value.y
        ocr.region_width, ocr.region_height = value.width, value.height
        self._settings.save(self._settings.current)

    def _region_interaction_started(self) -> None:
        self._region_interacting = True
        if self._session.is_running or self._ocr_active:
            self._stop_session()

    def _region_interaction_finished(self) -> None:
        self._region_interacting = False
        self._sync_region_to_target()

    def _sync_region_to_target(self) -> None:
        if self._region_interacting or not self._region_window.isVisible():
            return
        window = self._target.selected_window()
        ocr = self._settings.current.ocr
        if window is None or ocr.region_width <= 0 or ocr.region_height <= 0:
            return
        region = CaptureRegion(
            ocr.region_x,
            ocr.region_y,
            min(ocr.region_width, max(1, window.width - ocr.region_x)),
            min(ocr.region_height, max(1, window.height - ocr.region_y)),
        )
        self._region_window.set_target(window, region)

    def _close_region(self) -> None:
        if self._session.is_running or self._ocr_active:
            self._stop_session()
        ocr = self._settings.current.ocr
        ocr.region_x = ocr.region_y = ocr.region_width = ocr.region_height = 0
        self._settings.save(self._settings.current)
        self._page.set_status(self._tr("ctrl.ocr_region_removed"))
        self._set_visual_state("idle")

    def exit_ocr_tools(self) -> None:
        if self._session.is_running or self._ocr_active:
            self._stop_session()
        self._region_window.hide()
        self._orb.show_and_raise()

    def test_capture(self, mode: str = "auto") -> None:
        self._target.test_capture(mode)

    def preview_overlay_style(self, opacity: float, font_size: int, show_original: bool) -> None:
        self._overlay.apply_visual_preview(opacity, font_size, show_original)

    def show_overlay_preview(self) -> None:
        self._overlay.show()
        self._overlay.raise_()

    def reset_overlay_geometry(self) -> None:
        self._overlay.reset_geometry()
        self._overlay_geometry_changed(
            self._overlay.x(), self._overlay.y(), self._overlay.width(), self._overlay.height()
        )

    def _overlay_geometry_changed(self, x: int, y: int, width: int, height: int) -> None:
        self._page.mark_overlay_geometry_changed(x, y, width, height)
        self.overlay_geometry_changed.emit(x, y, width, height)

    def _preview_page_settings(self) -> None:
        ui = self._settings.current.ui
        try:
            self._page.collect_ui_settings(ui)
        except ValueError:
            return
        ui.ocr_overlay_x = self._overlay.x()
        ui.ocr_overlay_y = self._overlay.y()
        ui.ocr_overlay_width = self._overlay.width()
        ui.ocr_overlay_height = self._overlay.height()
        ui.ocr_orb_x = self._orb.x()
        ui.ocr_orb_y = self._orb.y()
        self._overlay.apply_settings(ui)
        self._orb.apply_settings(ui)
        self._ui_settings_timer.start()

    def _save_page_settings(self) -> None:
        try:
            self._settings.save(self._settings.current)
        except OSError as exc:
            self._page.set_status(self._tr("ctrl.settings.save_failed", error=str(exc)))

    def _save_orb_geometry(self, x: int, y: int) -> None:
        self._settings.current.ui.ocr_orb_x = x
        self._settings.current.ui.ocr_orb_y = y
        self._settings.save(self._settings.current)

    def _texts_ready(self, items: list[OcrText]) -> None:
        if not items:
            return
        self._set_visual_state("running")
        if self._settings.current.ocr.recognition_mode == "continuous":
            QTimer.singleShot(
                220,
                lambda: self._set_visual_state("waiting") if self._ocr_active else None,
            )
        route = self._settings.current.translation.ocr_route
        requests = [
            TranslationRequest(
                uuid4().hex,
                item.text,
                route.source_language,
                route.target_language,
                "ocr",
                self._translation_context.prepare(item.text),
            )
            for item in items
        ]
        accepted = self._scheduler.submit_many(requests)
        dropped = len(requests) - len(accepted)
        if dropped:
            self._logger.info("ocr_translation_dropped count=%s reason=queue_full", dropped)

    def _translation_completed(self, value: object) -> None:
        if not isinstance(value, OcrTranslationOutcome) or not self._ocr_active:
            return
        if value.result is not None:
            self._overlay.add_translation(value.result.original, value.result.translated)
            self._page.set_last_translation(
                value.result.original, value.result.translated
            )
            if not self._overlay.isVisible():
                self._overlay.show()
        else:
            error = value.error
            self._logger.warning(
                "ocr_translation_failed category=%s", getattr(error, "category", "unexpected")
            )
            message = (
                error.user_message
                if isinstance(error, VrcTranslateError)
                else self._tr("page.ocr.translation_failed", message=str(error))
            )
            self._page.set_status(message)
        if self._single_capture_finished and self._scheduler.pending_count == 0:
            self._finish_single()

    def _session_status(self, message: str) -> None:
        self._page.set_status(message)
        if self._settings.current.ocr.recognition_mode == "continuous" and self._ocr_active:
            self._set_visual_state("waiting")

    def _failed(self, message: str) -> None:
        self._session_failed = True
        self._ocr_active = False
        self._scheduler.stop()
        self._translation_context.clear()
        self._set_error(message)

    def _finished(self) -> None:
        if self._session_failed:
            self.tray_state_changed.emit("error")
            return
        if self._stopping:
            self._stopping = False
            self._ocr_active = False
            self._set_visual_state("idle")
            self._page.set_status(self._tr("ctrl.ocr_stopped"))
            self.tray_state_changed.emit("normal")
            if self._pending_start_mode is not None and not self._shutting_down:
                mode = self._pending_start_mode
                self._pending_start_mode = None
                QTimer.singleShot(0, lambda: self.start_mode(mode))
            return
        if self._settings.current.ocr.recognition_mode == "single":
            self._single_capture_finished = True
            if self._scheduler.pending_count == 0:
                self._finish_single()
            else:
                self._page.set_status(self._tr("ctrl.ocr_translating_result"))
            return
        self._ocr_active = False
        self._set_visual_state("idle")
        self.tray_state_changed.emit("normal")

    def _finish_single(self) -> None:
        self._single_capture_finished = False
        self._ocr_active = False
        self._scheduler.stop()
        self._translation_context.clear()
        self._set_visual_state("idle")
        self._page.set_status(self._tr("ctrl.ocr_single_finished"))
        self.tray_state_changed.emit("normal")

    def _set_visual_state(self, state: str) -> None:
        self._orb.set_state(state)
        self._region_window.set_state(state)

    def _set_error(self, message: str) -> None:
        self._page.set_status(message)
        self._set_visual_state("error")
        self.tray_state_changed.emit("error")

    def _update_capture_status(self) -> None:
        try:
            meaning = self._tr(
                "capture.status_window"
                if self._capture.semantics == "window_content"
                else "capture.status_screen"
            )
            self._page.set_status(self._tr("capture.prefix", backend=self._capture.backend_name) + meaning)
        except VrcTranslateError as exc:
            self._page.set_status(exc.user_message)

    def _capture_exclusion_warning(self) -> None:
        self._page.set_status(self._tr("ctrl.ocr_exclusion_failed"))
        self._logger.warning("ocr_overlay_capture_exclusion_unavailable")

    def shutdown(self, timeout_ms: int = 10_000) -> bool:
        self._shutting_down = True
        self._pending_start_mode = None
        self._ocr_active = False
        self._scheduler.shutdown()
        self._target_timer.stop()
        if self._ui_settings_timer.isActive():
            self._ui_settings_timer.stop()
            self._save_page_settings()
        self._translation_context.clear()
        self._overlay.close_permanently()
        self._orb.close_permanently()
        self._region_window.close_permanently()
        self._target.shutdown()
        return self._session.shutdown(timeout_ms)
