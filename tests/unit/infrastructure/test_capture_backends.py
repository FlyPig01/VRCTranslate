from __future__ import annotations

import numpy as np
import pytest

from vrctranslate.domain.ocr import CaptureRegion, WindowInfo
from vrctranslate.infrastructure.capture.mss_capture import MssScreenCapture
from vrctranslate.infrastructure.capture.capture_router import CaptureRouter
from vrctranslate.domain.errors import CaptureTargetUnavailable
from vrctranslate.infrastructure.capture.windows_graphics_capture import (
    WindowsGraphicsCapture,
)


class WindowsFake:
    def list_windows(self):
        return []

    def get_window(self, hwnd):
        return None

    def virtual_desktop(self):
        return WindowInfo(0, "Desktop", -1920, 0, 3840, 1080)


class WindowAvailableFake(WindowsFake):
    def get_window(self, hwnd):
        return WindowInfo(hwnd, "VRChat", 0, 0, 200, 100, "VRChat.exe", 123)


def test_mss_is_explicitly_screen_coordinate_capture() -> None:
    capture = MssScreenCapture(WindowsFake())
    assert capture.semantics == "screen_coordinates"
    assert capture.uses_screen_coordinates
    assert "屏幕坐标" in capture.backend_name
    assert capture.screen_target() == WindowInfo(
        0,
        "Desktop",
        -1920,
        0,
        3840,
        1080,
    )
    assert capture.get_window(0) == capture.screen_target()


def test_mss_captures_desktop_region_without_window_handle(monkeypatch) -> None:
    from vrctranslate.infrastructure.capture import mss_capture

    monitors: list[dict[str, int]] = []

    class Grabber:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def grab(self, monitor):
            monitors.append(monitor)
            return np.zeros((120, 300, 4), dtype=np.uint8)

    monkeypatch.setattr(mss_capture.mss, "mss", Grabber)

    frame = MssScreenCapture(WindowsFake()).capture(
        0,
        CaptureRegion(100, 50, 300, 120),
    )

    assert monitors == [
        {"left": -1820, "top": 50, "width": 300, "height": 120}
    ]
    assert frame.pixels.shape == (120, 300, 3)


def test_window_capture_scales_client_region_in_memory() -> None:
    pixels = np.zeros((200, 400, 3), dtype=np.uint8)
    window = WindowInfo(1, "VRChat", 0, 0, 200, 100)
    result = WindowsGraphicsCapture._crop_client_region(
        pixels, window, CaptureRegion(50, 25, 100, 50)
    )
    assert result.shape == (100, 200, 3)
    assert result.flags["C_CONTIGUOUS"]


def test_auto_mode_does_not_silently_fall_back_to_desktop(monkeypatch) -> None:
    monkeypatch.setattr(
        WindowsGraphicsCapture, "available", property(lambda _self: False)
    )
    windows = WindowsFake()
    router = CaptureRouter(WindowsGraphicsCapture(windows), MssScreenCapture(windows))
    with pytest.raises(CaptureTargetUnavailable, match="不会静默截取桌面"):
        _ = router.backend_name
    router.set_mode("screen")
    assert router.semantics == "screen_coordinates"
