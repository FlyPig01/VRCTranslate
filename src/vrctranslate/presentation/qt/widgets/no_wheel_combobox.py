from __future__ import annotations

from PySide6.QtCore import QPointF, Qt
from PySide6.QtGui import QColor, QPainter, QPen, QWheelEvent
from PySide6.QtWidgets import QComboBox, QStyle, QStyleOptionComboBox


class NoWheelComboBox(QComboBox):
    """A combo box that never changes a closed selection via mouse wheel."""

    def wheelEvent(self, event: QWheelEvent) -> None:
        if self.view().isVisible():
            super().wheelEvent(event)
        else:
            event.ignore()

    def paintEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().paintEvent(event)
        option = QStyleOptionComboBox()
        self.initStyleOption(option)
        arrow_rect = self.style().subControlRect(
            QStyle.ComplexControl.CC_ComboBox,
            option,
            QStyle.SubControl.SC_ComboBoxArrow,
            self,
        )
        if not arrow_rect.isValid():
            return

        center = arrow_rect.center()
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        pen = QPen(QColor("#1e708b" if self.isEnabled() else "#8d99a8"), 1.8)
        pen.setCapStyle(Qt.PenCapStyle.RoundCap)
        pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
        painter.setPen(pen)
        painter.drawLine(
            QPointF(center.x() - 4.0, center.y() - 2.0),
            QPointF(center.x(), center.y() + 2.0),
        )
        painter.drawLine(
            QPointF(center.x(), center.y() + 2.0),
            QPointF(center.x() + 4.0, center.y() - 2.0),
        )
