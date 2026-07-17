from __future__ import annotations

from typing import Protocol


class WindowCaptureExcluder(Protocol):
    def exclude_from_capture(self, hwnd: int) -> bool: ...


class WindowActivator(Protocol):
    def activate_window(self, hwnd: int) -> bool: ...
