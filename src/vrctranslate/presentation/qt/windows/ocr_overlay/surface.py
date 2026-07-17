from __future__ import annotations

from PySide6.QtCore import QRectF, Qt
from PySide6.QtGui import QColor, QPaintEvent, QPainter, QPen
from PySide6.QtWidgets import QWidget


class OverlaySurface(QWidget):
    """The single painted background, border and rounded shape of the overlay."""

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setObjectName("ocrOverlaySurface")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self._background_opacity = 0.88
        self._radius = 12.0

    @property
    def background_opacity(self) -> float:
        return self._background_opacity

    def set_background_opacity(self, opacity: float) -> None:
        value = min(1.0, max(0.0, float(opacity)))
        if abs(value - self._background_opacity) < 0.001:
            return
        self._background_opacity = value
        self.update()

    def paintEvent(self, event: QPaintEvent) -> None:
        del event
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing, True)
        rect = QRectF(self.rect()).adjusted(1.0, 1.0, -1.0, -1.0)
        background = QColor(18, 30, 48)
        background.setAlpha(round(255 * self._background_opacity))
        border = QColor(82, 195, 218, 190)
        painter.setBrush(background)
        painter.setPen(QPen(border, 1.5))
        painter.drawRoundedRect(rect, self._radius, self._radius)
