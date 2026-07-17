from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from uuid import uuid4

from PySide6.QtCore import QObject, QTimer, Signal


@dataclass(frozen=True, slots=True)
class OverlayEntry:
    entry_id: str
    original: str
    translated: str


class OverlayContentModel(QObject):
    """Own short-lived overlay entries independently from window rendering."""

    changed = Signal()

    def __init__(self, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self._entries: deque[OverlayEntry] = deque()
        self._timers: dict[str, QTimer] = {}
        self._maximum_items = 5
        self._display_seconds = 12.0

    @property
    def entries(self) -> tuple[OverlayEntry, ...]:
        return tuple(self._entries)

    @property
    def display_seconds(self) -> float:
        return self._display_seconds

    def configure(self, maximum_items: int, display_seconds: float) -> None:
        self._maximum_items = max(1, maximum_items)
        self._display_seconds = max(0.01, display_seconds)
        changed = False
        while len(self._entries) > self._maximum_items:
            removed = self._entries.popleft()
            self._stop_timer(removed.entry_id)
            changed = True
        if changed:
            self.changed.emit()

    def add(self, original: str, translated: str) -> None:
        text = translated.strip()
        if not text:
            return
        entry = OverlayEntry(uuid4().hex, original.strip(), text)
        self._entries.append(entry)
        while len(self._entries) > self._maximum_items:
            removed = self._entries.popleft()
            self._stop_timer(removed.entry_id)
        timer = QTimer(self)
        timer.setSingleShot(True)
        timer.timeout.connect(lambda entry_id=entry.entry_id: self._expire(entry_id))
        timer.start(max(1, int(self._display_seconds * 1000)))
        self._timers[entry.entry_id] = timer
        self.changed.emit()

    def clear(self) -> None:
        self._entries.clear()
        for timer in self._timers.values():
            timer.stop()
            timer.deleteLater()
        self._timers.clear()
        self.changed.emit()

    def _expire(self, entry_id: str) -> None:
        self._entries = deque(
            entry for entry in self._entries if entry.entry_id != entry_id
        )
        self._stop_timer(entry_id)
        self.changed.emit()

    def _stop_timer(self, entry_id: str) -> None:
        timer = self._timers.pop(entry_id, None)
        if timer is not None:
            timer.stop()
            timer.deleteLater()
