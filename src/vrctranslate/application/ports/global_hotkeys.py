from __future__ import annotations

from typing import Protocol


class GlobalHotkeys(Protocol):
    """Application-facing owner for platform global keyboard shortcuts."""

    def register(self, hwnd: int, hotkey_id: int, shortcut: str) -> bool: ...

    def unregister(self, hotkey_id: int) -> None: ...

    def shutdown(self) -> None: ...
