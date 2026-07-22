from __future__ import annotations

from math import sin

from PySide6.QtCore import QSize, QTimer, Qt
from PySide6.QtGui import QColor, QPaintEvent, QPainter
from PySide6.QtWidgets import QWidget


class VoiceActivityIndicator(QWidget):
    """Tiny animated waveform for microphone and recognition state."""

    _ANIMATED_STATES = {
        "calibrating",
        "listening",
        "recognizing",
        "translating",
        "testing",
    }

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._state = "idle"
        self._level = 0
        self._phase = 0
        self.setFixedSize(58, 34)
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self._timer = QTimer(self)
        self._timer.setInterval(80)
        self._timer.timeout.connect(self._advance)

    def sizeHint(self) -> QSize:
        return QSize(58, 34)

    def set_state(self, state: str) -> None:
        self._state = state
        if state in self._ANIMATED_STATES:
            if not self._timer.isActive():
                self._timer.start()
        else:
            self._timer.stop()
            self._phase = 0
        self.update()

    def set_level(self, level: int) -> None:
        self._level = min(100, max(0, int(level)))
        self.update()

    def _advance(self) -> None:
        self._phase = (self._phase + 1) % 1000
        self.update()

    def paintEvent(self, event: QPaintEvent) -> None:
        del event
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        color = {
            "error": QColor("#d4545f"),
            "calibrating": QColor("#d49336"),
            "recognizing": QColor("#7656b5"),
            "translating": QColor("#7656b5"),
            "success": QColor("#23835d"),
        }.get(self._state, QColor("#2388a0"))
        if self._state == "idle":
            color = QColor("#a8b5c6")
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(color)
        count = 5
        bar_width = 5
        gap = 5
        total = count * bar_width + (count - 1) * gap
        left = (self.width() - total) / 2
        center = self.height() / 2
        for index in range(count):
            if self._state in self._ANIMATED_STATES:
                wave = (sin(self._phase * 0.55 + index * 1.05) + 1) / 2
                microphone = self._level / 100 if self._state in {"listening", "testing"} else 0
                factor = max(wave * 0.72, microphone)
                height = 7 + factor * 22
            else:
                height = 7 + (self._level / 100) * 18
            x = left + index * (bar_width + gap)
            painter.drawRoundedRect(
                round(x),
                round(center - height / 2),
                bar_width,
                round(height),
                2.5,
                2.5,
            )
