from __future__ import annotations

from vrctranslate.infrastructure.capture.windows_api import WindowsApi


class _FakeUser32:
    def __init__(self, *, minimized: bool) -> None:
        self.minimized = minimized
        self.show_calls: list[tuple[int, int]] = []
        self.foreground_calls: list[int] = []

    @staticmethod
    def _value(hwnd: object) -> int:
        return int(getattr(hwnd, "value", hwnd))

    def IsIconic(self, _hwnd: object) -> bool:  # noqa: N802
        return self.minimized

    def ShowWindow(self, hwnd: object, command: int) -> bool:  # noqa: N802
        self.show_calls.append((self._value(hwnd), command))
        return True

    def SetForegroundWindow(self, hwnd: object) -> bool:  # noqa: N802
        self.foreground_calls.append(self._value(hwnd))
        return True


def _windows_api(user32: _FakeUser32) -> WindowsApi:
    api = WindowsApi.__new__(WindowsApi)
    api._user32 = user32
    return api


def test_activation_preserves_maximized_or_fullscreen_window_state() -> None:
    user32 = _FakeUser32(minimized=False)

    assert _windows_api(user32).activate_window(100)
    assert user32.show_calls == []
    assert user32.foreground_calls == [100]


def test_activation_restores_only_a_minimized_window() -> None:
    user32 = _FakeUser32(minimized=True)

    assert _windows_api(user32).activate_window(200)
    assert user32.show_calls == [(200, 9)]
    assert user32.foreground_calls == [200]
