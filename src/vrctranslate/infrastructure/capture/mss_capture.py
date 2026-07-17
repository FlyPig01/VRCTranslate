from __future__ import annotations

import mss
import numpy as np

from vrctranslate.domain.errors import CaptureTargetUnavailable
from vrctranslate.domain.ocr import CapturedFrame, CaptureRegion, WindowInfo
from vrctranslate.infrastructure.capture.windows_api import WindowsApi


class MssScreenCapture:
    """Explicit desktop-coordinate fallback; this is not HWND capture."""

    def __init__(self, windows: WindowsApi) -> None:
        self._windows = windows

    @property
    def backend_name(self) -> str:
        return "MSS 屏幕坐标兼容模式"

    @property
    def semantics(self) -> str:
        return "screen_coordinates"

    @property
    def uses_screen_coordinates(self) -> bool:
        return True

    def set_mode(self, _mode: str) -> None:
        return

    def list_windows(self) -> list[WindowInfo]:
        return self._windows.list_windows()

    def get_window(self, hwnd: int) -> WindowInfo | None:
        return self._windows.get_window(hwnd)

    def capture(self, hwnd: int, region: CaptureRegion) -> CapturedFrame:
        window = self._windows.get_window(hwnd)
        if window is None:
            raise CaptureTargetUnavailable("VRChat 窗口不可用，请重新选择窗口")
        x = max(0, region.x)
        y = max(0, region.y)
        width = region.width or window.width
        height = region.height or window.height
        width = min(width, window.width - x)
        height = min(height, window.height - y)
        if width < 10 or height < 10:
            raise CaptureTargetUnavailable("OCR 识别区域已经超出窗口范围")
        monitor = {
            "left": window.left + x,
            "top": window.top + y,
            "width": width,
            "height": height,
        }
        try:
            with mss.mss() as capture:
                raw = capture.grab(monitor)
            pixels = np.asarray(raw, dtype=np.uint8)[:, :, :3].copy()
        except Exception as exc:
            raise CaptureTargetUnavailable(
                f"无法捕获目标窗口：{type(exc).__name__}"
            ) from exc
        signature = pixels[::16, ::16, :].tobytes()
        return CapturedFrame(pixels=pixels, signature=signature)


# Backwards-compatible import for older callers. The explicit class name above
# prevents the adapter from being mistaken for true window capture.
WindowsMssCapture = MssScreenCapture
