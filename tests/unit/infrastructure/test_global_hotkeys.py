from __future__ import annotations

from vrctranslate.infrastructure.hotkeys import (
    WindowsGlobalHotkeys,
    parse_global_hotkey,
)


def _number(value: object) -> int:
    return int(getattr(value, "value", value) or 0)


class _Function:
    def __init__(self, result: bool = True) -> None:
        self.result = result
        self.calls: list[tuple[int, ...]] = []
        self.argtypes = None
        self.restype = None

    def __call__(self, *args: object) -> bool:
        self.calls.append(tuple(_number(value) for value in args))
        return self.result


class _User32:
    def __init__(self) -> None:
        self.RegisterHotKey = _Function()
        self.UnregisterHotKey = _Function()


def test_parse_global_hotkey_supports_default_and_function_keys() -> None:
    default = parse_global_hotkey("Ctrl+Alt+I")
    function = parse_global_hotkey("Ctrl+Shift+F12")

    assert default is not None and default[1] == ord("I")
    assert function is not None and function[1] == 0x7B
    assert parse_global_hotkey("") is None
    assert parse_global_hotkey("A") is None
    assert parse_global_hotkey("Ctrl+Alt+F25") is None


def test_register_replaces_existing_shortcut_and_shutdown_unregisters() -> None:
    user32 = _User32()
    manager = WindowsGlobalHotkeys(user32)

    assert manager.register(100, 7, "Ctrl+Alt+I")
    assert manager.register(100, 7, "Ctrl+Alt+M")
    assert len(user32.RegisterHotKey.calls) == 2
    assert user32.UnregisterHotKey.calls == [(100, 7)]

    manager.shutdown()
    assert user32.UnregisterHotKey.calls == [(100, 7), (100, 7)]


def test_empty_shortcut_disables_registration_and_conflict_is_reported() -> None:
    user32 = _User32()
    manager = WindowsGlobalHotkeys(user32)

    assert manager.register(100, 7, "")
    assert user32.RegisterHotKey.calls == []

    user32.RegisterHotKey.result = False
    assert not manager.register(100, 7, "Ctrl+Alt+I")
    assert user32.UnregisterHotKey.calls == []
