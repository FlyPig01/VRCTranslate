from __future__ import annotations

from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import QColor, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QProxyStyle, QStyle, QStyleOption, QWidget


class VrcTranslateStyle(QProxyStyle):
    """Application style additions that do not depend on OS theme assets."""

    def drawPrimitive(
        self,
        element: QStyle.PrimitiveElement,
        option: QStyleOption,
        painter: QPainter,
        widget: QWidget | None = None,
    ) -> None:
        if element != QStyle.PrimitiveElement.PE_IndicatorCheckBox:
            super().drawPrimitive(element, option, painter, widget)
            return

        rect = QRectF(option.rect).adjusted(1.0, 1.0, -1.0, -1.0)
        enabled = bool(option.state & QStyle.StateFlag.State_Enabled)
        checked = bool(option.state & QStyle.StateFlag.State_On)
        partial = bool(option.state & QStyle.StateFlag.State_NoChange)
        hovered = bool(option.state & QStyle.StateFlag.State_MouseOver)

        painter.save()
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        if checked or partial:
            fill = QColor("#2388a0" if hovered and enabled else "#1e708b")
            border = fill
        else:
            fill = QColor("#ffffff" if enabled else "#eef1f6")
            border = QColor("#8ea2ba" if hovered and enabled else "#a9b8ca")
        if not enabled:
            fill.setAlpha(150)
            border.setAlpha(150)

        painter.setBrush(fill)
        painter.setPen(QPen(border, 1.2))
        painter.drawRoundedRect(rect, 3.0, 3.0)

        mark_pen = QPen(QColor("#ffffff"), 1.9)
        mark_pen.setCapStyle(Qt.PenCapStyle.RoundCap)
        mark_pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
        painter.setPen(mark_pen)
        painter.setBrush(Qt.BrushStyle.NoBrush)
        if checked:
            path = QPainterPath(
                QPointF(rect.left() + rect.width() * 0.22, rect.center().y())
            )
            path.lineTo(
                QPointF(
                    rect.left() + rect.width() * 0.43,
                    rect.top() + rect.height() * 0.70,
                )
            )
            path.lineTo(
                QPointF(
                    rect.left() + rect.width() * 0.79,
                    rect.top() + rect.height() * 0.30,
                )
            )
            painter.drawPath(path)
        elif partial:
            y = rect.center().y()
            painter.drawLine(
                QPointF(rect.left() + rect.width() * 0.25, y),
                QPointF(rect.right() - rect.width() * 0.25, y),
            )
        painter.restore()
