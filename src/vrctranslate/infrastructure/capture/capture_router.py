from __future__ import annotations

from vrctranslate.domain.errors import CaptureTargetUnavailable
from vrctranslate.domain.ocr import CapturedFrame, CaptureRegion, WindowInfo
from vrctranslate.infrastructure.capture.mss_capture import MssScreenCapture
from vrctranslate.infrastructure.capture.windows_graphics_capture import (
    WindowsGraphicsCapture,
)


class CaptureRouter:
    """Select true HWND capture or an explicitly requested screen fallback."""

    def __init__(
        self,
        window_capture: WindowsGraphicsCapture,
        screen_capture: MssScreenCapture,
    ) -> None:
        self._window_capture = window_capture
        self._screen_capture = screen_capture
        self._mode = "auto"
        self._last_backend = ""
        self._last_error = ""
        self._last_signature: bytes | None = None
        self._last_size = (0, 0)
        self._last_black = False
        self._last_static = False
        self._last_target = ""

    def set_mode(self, mode: str) -> None:
        self._mode = mode if mode in {"auto", "windows", "screen"} else "auto"

    @property
    def backend_name(self) -> str:
        return self._selected().backend_name

    @property
    def semantics(self) -> str:
        return self._selected().semantics

    @property
    def uses_screen_coordinates(self) -> bool:
        return self._selected().uses_screen_coordinates

    @property
    def diagnostics(self) -> str:
        suffix = f"；最近错误：{self._last_error}" if self._last_error else ""
        frame = f"；帧：{self._last_size[0]}×{self._last_size[1]}"
        black = "；全黑帧" if self._last_black else ""
        static = "；与上一帧相同" if self._last_static else ""
        target = f"；目标：{self._last_target}" if self._last_target else ""
        return (
            f"后端：{self.backend_name}；语义：{self.semantics}"
            f"{target}{frame}{black}{static}{suffix}"
        )

    def list_windows(self) -> list[WindowInfo]:
        return self._window_capture.list_windows()

    def get_window(self, hwnd: int) -> WindowInfo | None:
        if self._mode == "screen" and hwnd == 0:
            return self._screen_capture.screen_target()
        return self._window_capture.get_window(hwnd)

    def screen_target(self) -> WindowInfo | None:
        if self._mode != "screen":
            return None
        return self._screen_capture.screen_target()

    def capture(self, hwnd: int, region: CaptureRegion) -> CapturedFrame:
        backend = self._selected()
        self._last_backend = backend.backend_name
        try:
            frame = backend.capture(hwnd, region)
            self._last_error = ""
            pixels = frame.pixels
            height, width = getattr(pixels, "shape", (0, 0))[:2]
            self._last_size = (int(width), int(height))
            try:
                self._last_black = bool(pixels.size and float(pixels.mean()) < 1.0)
            except Exception:
                self._last_black = False
            self._last_static = self._last_signature == frame.signature
            self._last_signature = frame.signature
            window = self.get_window(hwnd)
            if window is not None:
                self._last_target = (
                    f"HWND {window.hwnd} / {window.process_name or '未知进程'} / "
                    f"{window.width}×{window.height}"
                )
            return frame
        except CaptureTargetUnavailable as exc:
            self._last_error = exc.user_message
            raise

    def _selected(self) -> WindowsGraphicsCapture | MssScreenCapture:
        if self._mode == "screen":
            return self._screen_capture
        if not self._window_capture.available:
            raise CaptureTargetUnavailable(
                "Windows 窗口捕获组件不可用。自动模式不会静默截取桌面；如需回退，请显式选择 MSS 屏幕坐标兼容模式。"
            )
        return self._window_capture
