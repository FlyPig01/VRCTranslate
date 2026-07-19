from __future__ import annotations

import importlib.util
import inspect
from threading import Event
from typing import Any

import numpy as np

from vrctranslate.domain.errors import CaptureTargetUnavailable
from vrctranslate.domain.ocr import CapturedFrame, CaptureRegion, WindowInfo
from vrctranslate.infrastructure.capture.windows_api import WindowsApi


class WindowsGraphicsCapture:
    """In-memory Windows Graphics Capture adapter targeting a selected HWND."""

    def __init__(self, windows: WindowsApi, timeout_seconds: float = 3.0) -> None:
        self._windows = windows
        self._timeout = timeout_seconds
        self._last_frame_size = (0, 0)

    @property
    def available(self) -> bool:
        return importlib.util.find_spec("windows_capture") is not None

    @property
    def backend_name(self) -> str:
        return "Windows Graphics Capture"

    @property
    def semantics(self) -> str:
        return "window_content"

    @property
    def uses_screen_coordinates(self) -> bool:
        return False

    def set_mode(self, _mode: str) -> None:
        return

    def list_windows(self) -> list[WindowInfo]:
        return self._windows.list_windows()

    def get_window(self, hwnd: int) -> WindowInfo | None:
        return self._windows.get_window(hwnd)

    def screen_target(self) -> WindowInfo | None:
        return None

    def capture(self, hwnd: int, region: CaptureRegion) -> CapturedFrame:
        window = self._windows.get_window(hwnd)
        if window is None:
            raise CaptureTargetUnavailable("VRChat 窗口不可用，请重新选择窗口")
        if not self.available:
            raise CaptureTargetUnavailable(
                "当前环境缺少 Windows 窗口捕获组件；请安装项目依赖，或在设置中显式选择 MSS 兼容模式。"
            )
        pixels = self._capture_window(hwnd, window)
        pixels = self._crop_client_region(pixels, window, region)
        self._last_frame_size = (int(pixels.shape[1]), int(pixels.shape[0]))
        signature = pixels[::16, ::16, :].tobytes()
        return CapturedFrame(pixels=pixels, signature=signature)

    def _capture_window(self, hwnd: int, window: WindowInfo) -> np.ndarray:
        try:
            from windows_capture import WindowsCapture
        except Exception as exc:
            raise CaptureTargetUnavailable("Windows 窗口捕获组件加载失败") from exc

        received = Event()
        failure: list[Exception] = []
        result: list[np.ndarray] = []
        kwargs: dict[str, Any] = {
            "cursor_capture": False,
            "draw_border": False,
        }
        try:
            parameters = inspect.signature(WindowsCapture).parameters
        except (TypeError, ValueError):
            parameters = {}
        if "window_hwnd" in parameters:
            kwargs["window_hwnd"] = hwnd
        elif "window_handle" in parameters:
            kwargs["window_handle"] = hwnd
        else:
            # Current stable windows-capture resolves an exact title to HWND
            # internally. The HWND is revalidated immediately before capture.
            kwargs["window_name"] = window.title
        capture = WindowsCapture(**kwargs)

        @capture.event
        def on_frame_arrived(frame: Any, control: Any) -> None:
            try:
                raw = np.asarray(frame.frame_buffer, dtype=np.uint8)
                if raw.ndim != 3 or raw.shape[2] < 3:
                    raise ValueError("unexpected frame buffer")
                result.append(np.ascontiguousarray(raw[:, :, :3]))
            except Exception as exc:
                failure.append(exc)
            finally:
                received.set()
                control.stop()

        @capture.event
        def on_closed(*_args: object) -> None:
            received.set()

        try:
            if hasattr(capture, "start_free_threaded"):
                control = capture.start_free_threaded()
                if not received.wait(self._timeout):
                    control.stop()
                    raise CaptureTargetUnavailable("Windows 窗口捕获超时")
                control.stop()
            else:
                capture.start()
        except CaptureTargetUnavailable:
            raise
        except Exception as exc:
            raise CaptureTargetUnavailable(
                f"Windows 窗口捕获失败：{type(exc).__name__}"
            ) from exc
        if failure:
            raise CaptureTargetUnavailable("Windows 捕获帧格式无法读取") from failure[0]
        if not result:
            raise CaptureTargetUnavailable("Windows 窗口捕获没有返回画面")
        return result[0]

    @staticmethod
    def _crop_client_region(
        pixels: np.ndarray,
        window: WindowInfo,
        region: CaptureRegion,
    ) -> np.ndarray:
        frame_height, frame_width = pixels.shape[:2]
        scale_x = frame_width / max(window.width, 1)
        scale_y = frame_height / max(window.height, 1)
        x = max(0, round(region.x * scale_x))
        y = max(0, round(region.y * scale_y))
        width = round((region.width or window.width) * scale_x)
        height = round((region.height or window.height) * scale_y)
        right = min(frame_width, x + width)
        bottom = min(frame_height, y + height)
        if right - x < 10 or bottom - y < 10:
            raise CaptureTargetUnavailable("OCR 识别区域已经超出窗口范围")
        return np.ascontiguousarray(pixels[y:bottom, x:right, :3])
