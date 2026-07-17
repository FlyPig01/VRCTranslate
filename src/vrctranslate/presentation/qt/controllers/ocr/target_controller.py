from __future__ import annotations

import logging
from collections.abc import Callable

from PySide6.QtCore import QObject, QThreadPool, QTimer, Signal
from PySide6.QtWidgets import QMessageBox

from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.ocr import CaptureRegion, WindowInfo
from vrctranslate.presentation.qt.dialogs.region_selector import RegionSelector
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker
from vrctranslate.presentation.qt.i18n import I18nManager


class OcrTargetController(QObject):
    """Own window enumeration and client-relative region selection."""

    capture_preview_ready = Signal(object, str)

    def __init__(
        self,
        page: OcrPage,
        capture: FrameCapture,
        settings: ManageSettings,
        session_running: Callable[[], bool],
        logger: logging.Logger,
        i18n: I18nManager | None = None,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._capture = capture
        self._settings = settings
        self._session_running = session_running
        self._logger = logger
        self._i18n = i18n
        self._selector: RegionSelector | None = None
        self._selection_window: WindowInfo | None = None
        self._shutting_down = False
        self._preview_hid_main = False
        self._thread_pool = QThreadPool.globalInstance()

    def refresh_windows(self) -> None:
        try:
            windows = self._capture.list_windows()
        except Exception:
            windows = []
        self._page.set_windows(windows)
        if not windows:
            self._page.set_status(self._tr("ctrl.ocr.no_windows_found"))

    def _tr(self, key: str, **kwargs: object) -> str:
        if self._i18n is not None:
            return self._i18n.tr(key, **kwargs)
        return key

    def selected_window(self) -> WindowInfo | None:
        hwnd = self._page.selected_hwnd
        return self._capture.get_window(hwnd) if hwnd is not None else None

    def select_region(self) -> None:
        if self._session_running():
            QMessageBox.information(
                self._page,
                self._tr("ctrl.ocr.session_running"),
                self._tr("ctrl.ocr.session_running_msg"),
            )
            return
        window = self.selected_window()
        if window is None:
            QMessageBox.warning(
                self._page,
                self._tr("ctrl.ocr.cannot_select"),
                self._tr("ctrl.ocr.cannot_select_msg"),
            )
            return
        self._selection_window = window
        selector = RegionSelector(window, self._i18n)
        selector.selected.connect(self._region_selected)
        selector.cancelled.connect(self._region_cancelled)
        selector.destroyed.connect(lambda *_: setattr(self, "_selector", None))
        self._selector = selector
        self._page.window().hide()
        QTimer.singleShot(150, selector.show)

    def _region_selected(self, rect: object) -> None:
        if not hasattr(rect, "x"):
            return
        ocr = self._settings.current.ocr
        ocr.region_x = int(rect.x())  # type: ignore[attr-defined]
        ocr.region_y = int(rect.y())  # type: ignore[attr-defined]
        ocr.region_width = int(rect.width())  # type: ignore[attr-defined]
        ocr.region_height = int(rect.height())  # type: ignore[attr-defined]
        if self._selection_window:
            ocr.window_title = self._selection_window.title
        self._settings.save(self._settings.current)
        self._page.set_region(ocr)
        self.restore_main_window()
        self._page.set_status(self._tr("ctrl.ocr.region_saved"))

    def _region_cancelled(self) -> None:
        self._page.set_status(self._tr("ctrl.ocr.region_cancelled"))
        if not self._shutting_down:
            self.restore_main_window()

    def restore_main_window(self) -> None:
        window = self._page.window()
        window.show()
        window.raise_()
        window.activateWindow()

    def test_capture(self, mode: str = "auto") -> None:
        if self._session_running():
            self.capture_preview_ready.emit(None, self._tr("ctrl.ocr.test_needs_stop"))
            return
        window = self.selected_window()
        if window is None:
            self.capture_preview_ready.emit(None, self._tr("ctrl.ocr.test_needs_window"))
            return
        current = self._settings.current.ocr
        region = CaptureRegion(
            current.region_x,
            current.region_y,
            current.region_width or window.width,
            current.region_height or window.height,
        )
        self._capture.set_mode(mode)

        def begin() -> None:
            worker = TaskWorker(lambda: self._capture.capture(window.hwnd, region))
            worker.signals.succeeded.connect(self._capture_test_succeeded)
            worker.signals.failed.connect(self._capture_test_failed)
            self._thread_pool.start(worker)

        try:
            screen_capture = self._capture.uses_screen_coordinates
        except VrcTranslateError as exc:
            self.capture_preview_ready.emit(None, exc.user_message)
            return
        if screen_capture:
            self._preview_hid_main = True
            self._page.window().hide()
            QTimer.singleShot(180, begin)
        else:
            begin()

    def _capture_test_succeeded(self, value: object) -> None:
        pixels = getattr(value, "pixels", None)
        height, width = getattr(pixels, "shape", (0, 0))[:2]
        diagnostics = getattr(self._capture, "diagnostics", self._tr("ctrl.ocr.capture_ok"))
        self.capture_preview_ready.emit(
            pixels,
            self._tr("ctrl.ocr.capture_test_ok", width=width, height=height, diag=diagnostics),
        )
        self._restore_after_preview()

    def _capture_test_failed(self, error: object) -> None:
        message = (
            error.user_message
            if isinstance(error, VrcTranslateError)
            else self._tr("ctrl.ocr.capture_test_fail", name=type(error).__name__)
        )
        self.capture_preview_ready.emit(None, message)
        self._restore_after_preview()

    def _restore_after_preview(self) -> None:
        if self._preview_hid_main and not self._shutting_down:
            self._preview_hid_main = False
            self.restore_main_window()

    def shutdown(self) -> None:
        self._shutting_down = True
        if self._selector:
            self._selector.close()
