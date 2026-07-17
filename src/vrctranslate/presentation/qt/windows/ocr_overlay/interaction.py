from __future__ import annotations

from PySide6.QtCore import QPoint, Qt, Signal
from PySide6.QtGui import QMouseEvent
from PySide6.QtWidgets import QFrame, QHBoxLayout, QLabel, QSizeGrip, QWidget


class OverlayDragHandle(QFrame):
    """Use the native window manager for movement, with a Qt fallback."""

    interaction_started = Signal()
    interaction_finished = Signal()

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setObjectName("ocrOverlayDragHandle")
        self.setFixedHeight(30)
        layout = QHBoxLayout(self)
        layout.setContentsMargins(8, 0, 8, 0)
        self.label = QLabel("OCR")
        self.label.setObjectName("ocrOverlayHandleText")
        self.hint = QLabel("⋮⋮")
        self.hint.setObjectName("ocrOverlayHandleHint")
        layout.addWidget(self.label)
        layout.addStretch()
        layout.addWidget(self.hint)
        self._fallback_offset: QPoint | None = None

    def set_label(self, text: str) -> None:
        self.label.setText(text)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() != Qt.MouseButton.LeftButton:
            super().mousePressEvent(event)
            return
        self.interaction_started.emit()
        top_level = self.window()
        if self._start_system_move():
            event.accept()
            return
        self._fallback_offset = (
            event.globalPosition().toPoint() - top_level.frameGeometry().topLeft()
        )
        event.accept()

    def _start_system_move(self) -> bool:
        handle = self.window().windowHandle()
        return bool(handle is not None and handle.startSystemMove())

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if (
            self._fallback_offset is not None
            and event.buttons() & Qt.MouseButton.LeftButton
        ):
            self.window().move(
                event.globalPosition().toPoint() - self._fallback_offset
            )
            event.accept()
            return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._fallback_offset = None
            self.interaction_finished.emit()
            event.accept()
            return
        super().mouseReleaseEvent(event)


class OverlaySizeGrip(QSizeGrip):
    """Expose resize interaction boundaries so content updates can be deferred."""

    interaction_started = Signal()
    interaction_finished = Signal()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.interaction_started.emit()
        super().mousePressEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        super().mouseReleaseEvent(event)
        if event.button() == Qt.MouseButton.LeftButton:
            self.interaction_finished.emit()
