from __future__ import annotations

from PySide6.QtCore import QPoint, QRect, Qt, Signal
from PySide6.QtGui import QColor, QGuiApplication, QMouseEvent, QPaintEvent, QPainter, QPen
from PySide6.QtWidgets import QButtonGroup, QHBoxLayout, QLabel, QPushButton, QToolButton, QWidget

from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.domain.ocr import CaptureRegion, WindowInfo
from vrctranslate.presentation.qt.i18n import I18nManager


class OcrRegionWindow(QWidget):
    mode_requested = Signal(str)
    close_requested = Signal()
    region_changed = Signal(object)
    interaction_started = Signal()
    interaction_finished = Signal()

    BORDER = 6
    BAR_HEIGHT = 38
    MIN_CONTENT_WIDTH = 120
    MIN_CONTENT_HEIGHT = 50

    def __init__(
        self,
        capture_excluder: WindowCaptureExcluder | None = None,
        i18n: I18nManager | None = None,
    ) -> None:
        super().__init__(None)
        self._capture_excluder = capture_excluder
        self._i18n = i18n
        self._target: WindowInfo | None = None
        self._mode = "continuous"
        self._state = "idle"
        self._drag_origin: QPoint | None = None
        self._start_geometry = QRect()
        self._resize_edges = Qt.Edge(0)
        self._allow_close = False
        self._programmatic_geometry = False
        self._build_ui()
        if i18n is not None:
            i18n.language_changed.connect(lambda *_: self._retranslate())
        self._retranslate()

    def _build_ui(self) -> None:
        self.setObjectName("ocrRegionWindow")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setMouseTracking(True)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        self.setMinimumSize(
            self.MIN_CONTENT_WIDTH + self.BORDER * 2,
            self.MIN_CONTENT_HEIGHT + self.BAR_HEIGHT + self.BORDER,
        )
        self.bar = QWidget(self)
        self.bar.setObjectName("ocrRegionControlBar")
        bar_layout = QHBoxLayout(self.bar)
        bar_layout.setContentsMargins(10, 4, 5, 4)
        bar_layout.setSpacing(5)
        self.title_label = QLabel()
        self.title_label.setObjectName("ocrRegionTitle")
        self.state_label = QLabel()
        self.state_label.setObjectName("ocrRegionState")
        self.single_button = QPushButton()
        self.continuous_button = QPushButton()
        for button in (self.single_button, self.continuous_button):
            button.setCheckable(True)
            button.setObjectName("ocrRegionModeButton")
        self.mode_group = QButtonGroup(self)
        self.mode_group.setExclusive(True)
        self.mode_group.addButton(self.single_button)
        self.mode_group.addButton(self.continuous_button)
        self.single_button.clicked.connect(lambda: self.mode_requested.emit("single"))
        self.continuous_button.clicked.connect(lambda: self.mode_requested.emit("continuous"))
        self.close_button = QToolButton()
        self.close_button.setObjectName("ocrRegionCloseButton")
        self.close_button.setText("×")
        self.close_button.clicked.connect(self._request_close)
        bar_layout.addWidget(self.title_label)
        bar_layout.addWidget(self.state_label)
        bar_layout.addStretch()
        bar_layout.addWidget(self.single_button)
        bar_layout.addWidget(self.continuous_button)
        bar_layout.addWidget(self.close_button)
        self.set_mode("continuous")

    def _retranslate(self) -> None:
        if self._i18n is None:
            self.title_label.setText("OCR 识别区域")
            self.single_button.setText("单次")
            self.continuous_button.setText("持续")
            return
        t = self._i18n.tr
        self.title_label.setText(t("ocr_region.title"))
        self.single_button.setText(t("ocr_region.single"))
        self.continuous_button.setText(t("ocr_region.continuous"))
        self._update_state_text()

    def set_target(self, window: WindowInfo, region: CaptureRegion) -> None:
        self._target = window
        logical = self._logical_rect_for_region(window, region)
        self._programmatic_geometry = True
        self.setGeometry(
            logical.x() - self.BORDER,
            logical.y() - self.BAR_HEIGHT,
            logical.width() + self.BORDER * 2,
            logical.height() + self.BAR_HEIGHT + self.BORDER,
        )
        self._programmatic_geometry = False
        self.setWindowTitle(f"VRCTranslate OCR · {window.title}")

    def set_mode(self, mode: str) -> None:
        self._mode = "single" if mode == "single" else "continuous"
        self.single_button.setChecked(self._mode == "single")
        self.continuous_button.setChecked(self._mode == "continuous")

    def set_state(self, state: str) -> None:
        self._state = state
        self.setProperty("state", state)
        self._update_state_text()
        self.style().unpolish(self)
        self.style().polish(self)

    def _update_state_text(self) -> None:
        if self._i18n is None:
            self.state_label.setText(self._state)
            return
        key = {
            "idle": "ocr_region.state_idle",
            "running": "ocr_region.state_running",
            "waiting": "ocr_region.state_waiting",
            "error": "ocr_region.state_error",
        }.get(self._state, "ocr_region.state_idle")
        self.state_label.setText(self._i18n.tr(key))

    def toggle_visibility(self) -> None:
        if self.isVisible():
            self.hide()
        elif self._target is not None:
            self.show()
            self.raise_()

    def close_permanently(self) -> None:
        self._allow_close = True
        self.close()

    def paintEvent(self, event: QPaintEvent) -> None:
        del event
        painter = QPainter(self)
        try:
            content = QRect(
                self.BORDER // 2,
                self.BAR_HEIGHT - 1,
                self.width() - self.BORDER,
                self.height() - self.BAR_HEIGHT - self.BORDER // 2,
            )
            painter.setPen(QPen(Qt.GlobalColor.black, 5))
            painter.drawRect(content)
            color = QColor(
                {
                    "running": "#35d78a",
                    "waiting": "#f0b642",
                    "error": "#ff5f6d",
                }.get(self._state, "#55c7f2")
            )
            painter.setPen(QPen(color, 2.0))
            painter.drawRect(content.adjusted(1, 1, -1, -1))
        finally:
            painter.end()

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        self.bar.setGeometry(self.BORDER, 0, max(1, self.width() - self.BORDER * 2), self.BAR_HEIGHT)

    def showEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().showEvent(event)
        if self._capture_excluder is not None:
            self._capture_excluder.exclude_from_capture(int(self.winId()))

    def closeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        if self._allow_close:
            event.accept()
        else:
            self._request_close()
            event.ignore()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() != Qt.MouseButton.LeftButton:
            return
        self._drag_origin = event.globalPosition().toPoint()
        self._start_geometry = self.geometry()
        self._resize_edges = self._edges_at(event.position().toPoint())
        self.interaction_started.emit()
        event.accept()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        point = event.position().toPoint()
        if self._drag_origin is None:
            self._update_cursor(self._edges_at(point))
            return
        delta = event.globalPosition().toPoint() - self._drag_origin
        geometry = QRect(self._start_geometry)
        if not self._resize_edges:
            geometry.translate(delta)
        else:
            if self._resize_edges & Qt.Edge.LeftEdge:
                geometry.setLeft(geometry.left() + delta.x())
            if self._resize_edges & Qt.Edge.RightEdge:
                geometry.setRight(geometry.right() + delta.x())
            if self._resize_edges & Qt.Edge.TopEdge:
                geometry.setTop(geometry.top() + delta.y())
            if self._resize_edges & Qt.Edge.BottomEdge:
                geometry.setBottom(geometry.bottom() + delta.y())
            geometry.setWidth(max(self.minimumWidth(), geometry.width()))
            geometry.setHeight(max(self.minimumHeight(), geometry.height()))
        self.setGeometry(geometry)
        event.accept()

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if event.button() != Qt.MouseButton.LeftButton or self._drag_origin is None:
            return
        self._drag_origin = None
        self._resize_edges = Qt.Edge(0)
        region = self.current_region()
        if region is not None and self._target is not None:
            region = self._clamp_region(region, self._target)
            self.set_target(self._target, region)
            self.region_changed.emit(region)
        self.interaction_finished.emit()
        event.accept()

    def current_region(self) -> CaptureRegion | None:
        target = self._target
        if target is None:
            return None
        content_top_left = QPoint(self.x() + self.BORDER, self.y() + self.BAR_HEIGHT)
        physical_x, physical_y, ratio = self._physical_point(content_top_left)
        return CaptureRegion(
            max(0, physical_x - target.left),
            max(0, physical_y - target.top),
            max(1, round((self.width() - self.BORDER * 2) * ratio)),
            max(1, round((self.height() - self.BAR_HEIGHT - self.BORDER) * ratio)),
        )

    def _edges_at(self, point: QPoint) -> Qt.Edge:
        margin = 9
        edges = Qt.Edge(0)
        if point.x() <= margin:
            edges |= Qt.Edge.LeftEdge
        elif point.x() >= self.width() - margin:
            edges |= Qt.Edge.RightEdge
        if point.y() <= margin:
            edges |= Qt.Edge.TopEdge
        elif point.y() >= self.height() - margin:
            edges |= Qt.Edge.BottomEdge
        return edges

    def _update_cursor(self, edges: Qt.Edge) -> None:
        if edges in (Qt.Edge.LeftEdge, Qt.Edge.RightEdge):
            self.setCursor(Qt.CursorShape.SizeHorCursor)
        elif edges in (Qt.Edge.TopEdge, Qt.Edge.BottomEdge):
            self.setCursor(Qt.CursorShape.SizeVerCursor)
        elif edges in (
            Qt.Edge.LeftEdge | Qt.Edge.TopEdge,
            Qt.Edge.RightEdge | Qt.Edge.BottomEdge,
        ):
            self.setCursor(Qt.CursorShape.SizeFDiagCursor)
        elif edges:
            self.setCursor(Qt.CursorShape.SizeBDiagCursor)
        else:
            self.setCursor(Qt.CursorShape.ArrowCursor)

    def _request_close(self) -> None:
        self.hide()
        self.close_requested.emit()

    @staticmethod
    def _clamp_region(region: CaptureRegion, target: WindowInfo) -> CaptureRegion:
        x = min(max(0, region.x), max(0, target.width - 1))
        y = min(max(0, region.y), max(0, target.height - 1))
        return CaptureRegion(
            x,
            y,
            min(region.width, target.width - x),
            min(region.height, target.height - y),
        )

    @classmethod
    def _logical_rect_for_region(cls, target: WindowInfo, region: CaptureRegion) -> QRect:
        x, y, ratio, logical_origin = cls._logical_point(target.left + region.x, target.top + region.y)
        del logical_origin
        return QRect(x, y, max(1, round(region.width / ratio)), max(1, round(region.height / ratio)))

    @staticmethod
    def _logical_point(physical_x: int, physical_y: int) -> tuple[int, int, float, QPoint]:
        for screen in QGuiApplication.screens():
            geo = screen.geometry()
            ratio = screen.devicePixelRatio()
            physical_left = round(geo.x() * ratio)
            physical_top = round(geo.y() * ratio)
            physical_width = round(geo.width() * ratio)
            physical_height = round(geo.height() * ratio)
            if physical_left <= physical_x < physical_left + physical_width and physical_top <= physical_y < physical_top + physical_height:
                return (
                    geo.x() + round((physical_x - physical_left) / ratio),
                    geo.y() + round((physical_y - physical_top) / ratio),
                    ratio,
                    geo.topLeft(),
                )
        return physical_x, physical_y, 1.0, QPoint()

    @staticmethod
    def _physical_point(logical: QPoint) -> tuple[int, int, float]:
        screen = QGuiApplication.screenAt(logical) or QGuiApplication.primaryScreen()
        if screen is None:
            return logical.x(), logical.y(), 1.0
        geo = screen.geometry()
        ratio = screen.devicePixelRatio()
        physical_left = round(geo.x() * ratio)
        physical_top = round(geo.y() * ratio)
        return (
            physical_left + round((logical.x() - geo.x()) * ratio),
            physical_top + round((logical.y() - geo.y()) * ratio),
            ratio,
        )
