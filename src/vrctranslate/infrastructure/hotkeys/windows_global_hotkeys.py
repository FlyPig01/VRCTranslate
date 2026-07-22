from __future__ import annotations

import ctypes
from ctypes import wintypes


_MOD_ALT = 0x0001
_MOD_CONTROL = 0x0002
_MOD_SHIFT = 0x0004
_MOD_WIN = 0x0008
_MOD_NOREPEAT = 0x4000


def parse_global_hotkey(value: str) -> tuple[int, int] | None:
    parts = [part.strip() for part in str(value).split("+") if part.strip()]
    if len(parts) < 2:
        return None
    modifiers = 0
    key = ""
    for part in parts:
        folded = part.casefold()
        if folded in {"ctrl", "control"}:
            modifiers |= _MOD_CONTROL
        elif folded == "alt":
            modifiers |= _MOD_ALT
        elif folded == "shift":
            modifiers |= _MOD_SHIFT
        elif folded in {"meta", "win", "windows"}:
            modifiers |= _MOD_WIN
        elif key:
            return None
        else:
            key = part.upper()
    if not modifiers or not key:
        return None
    if len(key) == 1 and ("A" <= key <= "Z" or "0" <= key <= "9"):
        virtual_key = ord(key)
    elif key.startswith("F") and key[1:].isdigit():
        number = int(key[1:])
        if not 1 <= number <= 24:
            return None
        virtual_key = 0x70 + number - 1
    else:
        return None
    return modifiers | _MOD_NOREPEAT, virtual_key


class WindowsGlobalHotkeys:
    """Small RegisterHotKey owner; Qt handles the resulting WM_HOTKEY."""

    def __init__(self, user32: object | None = None) -> None:
        self._user32 = user32 or ctypes.WinDLL("user32", use_last_error=True)
        try:
            self._user32.RegisterHotKey.argtypes = [
                wintypes.HWND,
                ctypes.c_int,
                wintypes.UINT,
                wintypes.UINT,
            ]
            self._user32.RegisterHotKey.restype = wintypes.BOOL
            self._user32.UnregisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int]
            self._user32.UnregisterHotKey.restype = wintypes.BOOL
        except AttributeError:
            pass
        self._hwnd = 0
        self._registered: set[int] = set()

    def register(self, hwnd: int, hotkey_id: int, shortcut: str) -> bool:
        self.unregister(hotkey_id)
        parsed = parse_global_hotkey(shortcut)
        if parsed is None:
            return not str(shortcut).strip()
        modifiers, virtual_key = parsed
        self._hwnd = int(hwnd)
        if not self._user32.RegisterHotKey(
            wintypes.HWND(self._hwnd),
            int(hotkey_id),
            wintypes.UINT(modifiers),
            wintypes.UINT(virtual_key),
        ):
            return False
        self._registered.add(int(hotkey_id))
        return True

    def unregister(self, hotkey_id: int) -> None:
        value = int(hotkey_id)
        if value not in self._registered:
            return
        try:
            self._user32.UnregisterHotKey(wintypes.HWND(self._hwnd), value)
        finally:
            self._registered.discard(value)

    def shutdown(self) -> None:
        for hotkey_id in tuple(self._registered):
            self.unregister(hotkey_id)
