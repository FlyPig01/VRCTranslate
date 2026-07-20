from __future__ import annotations

from PySide6.QtCore import QEvent, QObject, Qt
from PySide6.QtGui import QKeyEvent


class TabKeyFilter(QObject):
    """Disable Tab-based focus traversal across every application window."""

    def eventFilter(self, watched: QObject, event: QEvent) -> bool:
        if (
            event.type()
            in {
                QEvent.Type.KeyPress,
                QEvent.Type.KeyRelease,
                QEvent.Type.ShortcutOverride,
            }
            and isinstance(event, QKeyEvent)
            and event.key() in {Qt.Key.Key_Tab, Qt.Key.Key_Backtab}
        ):
            event.accept()
            return True
        return super().eventFilter(watched, event)
