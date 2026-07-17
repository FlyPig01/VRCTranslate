from __future__ import annotations

import logging
from collections.abc import Callable

from PySide6.QtCore import QObject, QThreadPool, QTimer, Signal
from PySide6.QtWidgets import QMessageBox, QWidget

from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.ports.window_capture import WindowActivator
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.ocr import CaptureRegion, WindowInfo
from vrctranslate.presentation.qt.dialogs.region_selector import RegionSelector
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker


class OcrTargetController(QObject):
    """Own target discovery and client-relative selection without page controls."""

    capture_preview_ready = Signal(object, str)
    target_changed = Signal(object)
    region_selected = Signal(object)

    def __init__(
        self,
        parent_widget: QWidget,
        capture: FrameCapture,
        settings: ManageSettings,
        session_running: Callable[[], bool],
        logger: logging.Logger,
        windows_api: WindowActivator,
        i18n: I18nManager | None = None,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._parent_widget = parent_widget
        self._capture = capture
        self._settings = settings
        self._session_running = session_running
        self._logger = logger
        self._windows_api = windows_api
        self._i18n = i18n
        self._selector: RegionSelector | None = None
        self._selected: WindowInfo | None = None
        self._thread_pool = QThreadPool.globalInstance()
        self._shutting_down = False

    def _tr(self, key: str, **kwargs: object) -> str:
        return self._i18n.tr(key, **kwargs) if self._i18n is not None else key

    def refresh_windows(self) -> list[WindowInfo]:
        try:
            windows = self._capture.list_windows()
        except Exception:
            windows = []
        if self._selected is not None:
            refreshed = next((item for item in windows if item.hwnd == self._selected.hwnd), None)
            if refreshed is not None:
                self._selected = refreshed
                return windows
        title = self._settings.current.ocr.window_title.casefold()
        self._selected = next((item for item in windows if item.title.casefold() == title), None)
        if self._selected is None and title in {"", "vrchat"}:
            self._selected = next(
                (
                    item
                    for item in windows
                    if "vrchat" in f"{item.title} {item.process_name}".casefold()
                ),
                None,
            )
        if self._selected is not None:
            self.target_changed.emit(self._selected)
        return windows

    def select_window(self, hwnd: int) -> WindowInfo | None:
        """Select a target chosen from the OCR page without opening a dialog."""

        if self._session_running():
            return None
        windows = self.refresh_windows()
        selected = next((item for item in windows if item.hwnd == hwnd), None)
        if selected is None:
            return None
        self._selected = selected
        self.target_changed.emit(selected)
        return selected

    def selected_window(self) -> WindowInfo | None:
        if self._selected is None:
            self.refresh_windows()
        if self._selected is None:
            return None
        refreshed = self._capture.get_window(self._selected.hwnd)
        if refreshed is None:
            self._selected = None
            self.refresh_windows()
            return self._selected
        self._selected = refreshed
        return refreshed

    def select_region(self) -> None:
        if self._session_running():
            QMessageBox.information(
                self._parent_widget,
                self._tr("ctrl.ocr.session_running"),
                self._tr("ctrl.ocr.session_running_msg"),
            )
            return
        window = self.selected_window()
        if window is None:
            QMessageBox.warning(
                self._parent_widget,
                self._tr("ctrl.ocr_no_window"),
                self._tr("ctrl.ocr.test_needs_window"),
            )
            return
        self._windows_api.activate_window(window.hwnd)
        selector = RegionSelector(window, self._i18n)
        selector.selected.connect(self._selection_completed)
        selector.cancelled.connect(self._selection_cancelled)
        selector.destroyed.connect(lambda *_: setattr(self, "_selector", None))
        self._selector = selector
        QTimer.singleShot(120, selector.show)

    def _selection_completed(self, rect: object) -> None:
        if not hasattr(rect, "x"):
            return
        region = CaptureRegion(
            int(rect.x()),  # type: ignore[attr-defined]
            int(rect.y()),  # type: ignore[attr-defined]
            int(rect.width()),  # type: ignore[attr-defined]
            int(rect.height()),  # type: ignore[attr-defined]
        )
        self.region_selected.emit(region)

    def _selection_cancelled(self) -> None:
        self._logger.info("ocr_region_selection_cancelled")

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
        self._windows_api.activate_window(window.hwnd)

        def begin() -> None:
            worker = TaskWorker(lambda: self._capture.capture(window.hwnd, region))
            worker.signals.succeeded.connect(self._capture_test_succeeded)
            worker.signals.failed.connect(self._capture_test_failed)
            self._thread_pool.start(worker)

        QTimer.singleShot(120, begin)

    def _capture_test_succeeded(self, value: object) -> None:
        pixels = getattr(value, "pixels", None)
        height, width = getattr(pixels, "shape", (0, 0))[:2]
        diagnostics = getattr(self._capture, "diagnostics", self._tr("ctrl.ocr.capture_ok"))
        self.capture_preview_ready.emit(
            pixels,
            self._tr("ctrl.ocr.capture_test_ok", width=width, height=height, diag=diagnostics),
        )

    def _capture_test_failed(self, error: object) -> None:
        message = (
            error.user_message
            if isinstance(error, VrcTranslateError)
            else self._tr("ctrl.ocr.capture_test_fail", name=type(error).__name__)
        )
        self.capture_preview_ready.emit(None, message)

    def shutdown(self) -> None:
        self._shutting_down = True
        if self._selector:
            self._selector.close()
