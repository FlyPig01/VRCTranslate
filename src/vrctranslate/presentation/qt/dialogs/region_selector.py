from __future__ import annotations

from PySide6.QtCore import QPoint, QRect, Qt, Signal
from PySide6.QtGui import (
    QColor,
    QCloseEvent,
    QCursor,
    QGuiApplication,
    QKeyEvent,
    QMouseEvent,
    QPainter,
    QPen,
)
from PySide6.QtWidgets import QWidget

from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.presentation.qt.i18n import I18nManager


class RegionSelector(QWidget):
    selected = Signal(object)
    cancelled = Signal()

    def __init__(self, window: WindowInfo, i18n: I18nManager | None = None) -> None:
        super().__init__(None)
        self._i18n = i18n
        self._client_width = window.width
        self._client_height = window.height
        self._origin: QPoint | None = None
        self._selection = QRect()
        self._cursor_position: QPoint | None = None
        self._completed = False
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.BlankCursor)
        scale = self._dpi_scale_at(window.left, window.top)
        self.setGeometry(
            round(window.left / scale),
            round(window.top / scale),
            round(window.width / scale),
            round(window.height / scale),
        )
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)

    @staticmethod
    def _dpi_scale_at(physical_x: int, physical_y: int) -> float:
        """获取指定物理屏幕坐标处的 DPI 缩放比例。"""
        for screen in QGuiApplication.screens():
            geo = screen.geometry()
            ratio = screen.devicePixelRatio()
            phys_x = round(geo.x() * ratio)
            phys_y = round(geo.y() * ratio)
            phys_w = round(geo.width() * ratio)
            phys_h = round(geo.height() * ratio)
            if phys_x <= physical_x < phys_x + phys_w and phys_y <= physical_y < phys_y + phys_h:
                return ratio
        primary = QGuiApplication.primaryScreen()
        return primary.devicePixelRatio() if primary else 1.0

    def showEvent(self, event: object) -> None:
        super().showEvent(event)  # type: ignore[arg-type]
        cursor_position = self.mapFromGlobal(QCursor.pos())
        if self.rect().contains(cursor_position):
            self._cursor_position = cursor_position
        self.activateWindow()
        self.setFocus()

    def paintEvent(self, event: object) -> None:
        del event
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor(0, 0, 0, 110))
        if not self._selection.isNull():
            painter.setCompositionMode(QPainter.CompositionMode.CompositionMode_Clear)
            painter.fillRect(self._selection, Qt.GlobalColor.transparent)
            painter.setCompositionMode(QPainter.CompositionMode.CompositionMode_SourceOver)
            painter.setPen(QPen(QColor(66, 165, 245), 2))
            painter.drawRect(self._selection)
        painter.setPen(QColor(255, 255, 255))
        hint = (
            self._i18n.tr("region_selector.hint")
            if self._i18n
            else "拖动选择 OCR 区域；按 Esc 取消。区域越小识别越快越准，画面不会保存到磁盘。"
        )
        painter.drawText(16, 28, hint)
        self._paint_crosshair(painter)

    def _paint_crosshair(self, painter: QPainter) -> None:
        point = self._cursor_position
        if point is None or not self.rect().contains(point):
            return
        painter.save()
        try:
            # Thin full-frame guides make the pointer easy to locate, while a
            # black outline and bright centre remain visible on any game scene.
            guide_lines = (
                (QPoint(0, point.y()), QPoint(self.width(), point.y())),
                (QPoint(point.x(), 0), QPoint(point.x(), self.height())),
            )
            for color, width in (
                (QColor(0, 0, 0, 210), 3),
                (QColor(255, 255, 255, 225), 1),
            ):
                pen = QPen(color, width, Qt.PenStyle.DashLine)
                painter.setPen(pen)
                for start, end in guide_lines:
                    painter.drawLine(start, end)

            arm = 24
            gap = 5
            arms = (
                (QPoint(point.x() - arm, point.y()), QPoint(point.x() - gap, point.y())),
                (QPoint(point.x() + gap, point.y()), QPoint(point.x() + arm, point.y())),
                (QPoint(point.x(), point.y() - arm), QPoint(point.x(), point.y() - gap)),
                (QPoint(point.x(), point.y() + gap), QPoint(point.x(), point.y() + arm)),
            )
            for color, width in (
                (QColor(0, 0, 0, 255), 6),
                (QColor(84, 214, 255, 255), 3),
            ):
                painter.setPen(QPen(color, width, Qt.PenStyle.SolidLine))
                for start, end in arms:
                    painter.drawLine(start, end)
            painter.setPen(QPen(QColor(0, 0, 0), 2))
            painter.setBrush(QColor(84, 214, 255))
            painter.drawEllipse(point, 3, 3)
        finally:
            painter.restore()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._cursor_position = event.position().toPoint()
            self._origin = self._cursor_position
            self._selection = QRect(self._origin, self._origin)
            self.update()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        self._cursor_position = event.position().toPoint()
        if self._origin is not None:
            self._selection = QRect(
                self._origin, self._cursor_position
            ).normalized().intersected(self.rect())
        self.update()

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if event.button() != Qt.MouseButton.LeftButton or self._origin is None:
            return
        self._cursor_position = event.position().toPoint()
        self._selection = QRect(
            self._origin, self._cursor_position
        ).normalized().intersected(self.rect())
        self._origin = None
        if self._selection.width() >= 20 and self._selection.height() >= 20:
            self._completed = True
            self.selected.emit(self.client_rect_from_widget(self._selection))
            self.close()
        else:
            self._selection = QRect()
            self.update()

    def keyPressEvent(self, event: QKeyEvent) -> None:
        if event.key() == Qt.Key.Key_Escape:
            self._completed = True
            self.cancelled.emit()
            self.close()
            return
        super().keyPressEvent(event)

    def closeEvent(self, event: QCloseEvent) -> None:
        if not self._completed:
            self._completed = True
            self.cancelled.emit()
        event.accept()

    def client_rect_from_widget(self, rect: QRect) -> QRect:
        """Map Qt logical widget coordinates into the Win32 client space.

        Qt may resize the selector geometry under per-monitor DPI scaling. The
        saved OCR region must use the same client coordinate space returned by
        WindowsApi rather than assuming both sizes are identical.
        """
        widget_width = max(self.width(), 1)
        widget_height = max(self.height(), 1)
        scale_x = self._client_width / widget_width
        scale_y = self._client_height / widget_height
        x = round(rect.x() * scale_x)
        y = round(rect.y() * scale_y)
        width = round(rect.width() * scale_x)
        height = round(rect.height() * scale_y)
        return QRect(
            max(0, x),
            max(0, y),
            min(width, self._client_width - max(0, x)),
            min(height, self._client_height - max(0, y)),
        )
