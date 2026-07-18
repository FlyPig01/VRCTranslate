from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from uuid import uuid4

from PySide6.QtCore import QObject, Signal


@dataclass(frozen=True, slots=True)
class OverlayEntry:
    entry_id: str
    original: str
    translated: str
    group_id: str = ""


class OverlayContentModel(QObject):
    """Keep one complete OCR result group until the next valid group arrives."""

    changed = Signal()

    def __init__(self, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self._entries: deque[OverlayEntry] = deque()
        self._maximum_items = 5
        self._active_group_id = ""

    @property
    def entries(self) -> tuple[OverlayEntry, ...]:
        return tuple(self._entries)

    def configure(self, maximum_items: int) -> None:
        self._maximum_items = max(1, maximum_items)
        changed = False
        while len(self._entries) > self._maximum_items:
            self._entries.popleft()
            changed = True
        if changed:
            self.changed.emit()

    def add(self, original: str, translated: str, group_id: str = "") -> None:
        text = translated.strip()
        if not text:
            return
        if group_id and group_id != self._active_group_id:
            self._entries.clear()
            self._active_group_id = group_id
        entry = OverlayEntry(uuid4().hex, original.strip(), text, group_id)
        self._entries.append(entry)
        while len(self._entries) > self._maximum_items:
            self._entries.popleft()
        self.changed.emit()

    def clear(self) -> None:
        self._entries.clear()
        self._active_group_id = ""
        self.changed.emit()
