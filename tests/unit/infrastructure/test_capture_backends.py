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


class WindowAvailableFake(WindowsFake):
    def get_window(self, hwnd):
        return WindowInfo(hwnd, "VRChat", 0, 0, 200, 100, "VRChat.exe", 123)


def test_mss_is_explicitly_screen_coordinate_capture() -> None:
    capture = MssScreenCapture(WindowsFake())
    assert capture.semantics == "screen_coordinates"
    assert capture.uses_screen_coordinates
    assert "屏幕坐标" in capture.backend_name


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
