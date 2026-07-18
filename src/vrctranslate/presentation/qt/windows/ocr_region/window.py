from __future__ import annotations

from PySide6.QtCore import QPoint, QRect, Qt, Signal
from PySide6.QtGui import (
    QAction,
    QActionGroup,
    QColor,
    QGuiApplication,
    QMouseEvent,
    QPaintEvent,
    QPainter,
    QPen,
)
from PySide6.QtWidgets import (
    QButtonGroup,
    QHBoxLayout,
    QLabel,
    QMenu,
    QPushButton,
    QToolButton,
    QWidget,
)

from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.domain.ocr import CaptureRegion, WindowInfo
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.windows.ocr_geometry import (
    logical_rect_for_region,
    physical_point,
)


class OcrRegionWindow(QWidget):
    mode_requested = Signal(str)
    close_requested = Signal()
    region_changed = Signal(object)
    interaction_started = Signal()
    interaction_finished = Signal()
    display_mode_requested = Signal(str)

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
        self._error_message = ""
        self._display_mode = "overlay"
        self._drag_origin: QPoint | None = None
        self._start_geometry = QRect()
        self._resize_edges = Qt.Edge(0)
        self._content_rect = QRect()
        self._bar_inside = False
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
        self.bar.setCursor(Qt.CursorShape.ArrowCursor)
        bar_layout = QHBoxLayout(self.bar)
        bar_layout.setContentsMargins(10, 4, 5, 4)
        bar_layout.setSpacing(5)
        self.title_label = QLabel()
        self.title_label.setObjectName("ocrRegionTitle")
        self.title_label.setCursor(Qt.CursorShape.ArrowCursor)
        self.state_label = QLabel()
        self.state_label.setObjectName("ocrRegionState")
        self.state_label.setCursor(Qt.CursorShape.ArrowCursor)
        self.single_button = QPushButton()
        self.continuous_button = QPushButton()
        for button in (self.single_button, self.continuous_button):
            button.setCheckable(True)
            button.setObjectName("ocrRegionModeButton")
            button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.mode_group = QButtonGroup(self)
        self.mode_group.setExclusive(True)
        self.mode_group.addButton(self.single_button)
        self.mode_group.addButton(self.continuous_button)
        self.single_button.clicked.connect(lambda: self.mode_requested.emit("single"))
        self.continuous_button.clicked.connect(lambda: self.mode_requested.emit("continuous"))
        self.display_button = QToolButton()
        self.display_button.setObjectName("ocrRegionDisplayButton")
        self.display_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.display_button.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        self.display_menu = QMenu(self.display_button)
        self.display_group = QActionGroup(self)
        self.display_group.setExclusive(True)
        self.display_actions: dict[str, QAction] = {}
        for mode in ("overlay", "inline", "both"):
            action = QAction(self.display_menu)
            action.setCheckable(True)
            action.triggered.connect(
                lambda _checked=False, value=mode: self._request_display_mode(value)
            )
            self.display_group.addAction(action)
            self.display_menu.addAction(action)
            self.display_actions[mode] = action
        self.display_button.setMenu(self.display_menu)
        self.close_button = QToolButton()
        self.close_button.setObjectName("ocrRegionCloseButton")
        self.close_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.close_button.setText("×")
        self.close_button.clicked.connect(self._request_close)
        bar_layout.addWidget(self.title_label)
        bar_layout.addWidget(self.state_label)
        bar_layout.addStretch()
        bar_layout.addWidget(self.single_button)
        bar_layout.addWidget(self.continuous_button)
        bar_layout.addWidget(self.display_button)
        bar_layout.addWidget(self.close_button)
        self.set_mode("continuous")
        self.set_display_mode("overlay")

    def _retranslate(self) -> None:
        if self._i18n is None:
            self.title_label.setText("OCR 识别区域")
            self.single_button.setText("单次")
            self.continuous_button.setText("持续")
            self.display_button.setText("译文")
            labels = {
                "overlay": "独立浮窗",
                "inline": "区域嵌字",
                "both": "浮窗与嵌字",
            }
            for mode, action in self.display_actions.items():
                action.setText(labels[mode])
            return
        t = self._i18n.tr
        self.title_label.setText(t("ocr_region.title"))
        self.single_button.setText(t("ocr_region.single"))
        self.continuous_button.setText(t("ocr_region.continuous"))
        self.display_button.setText(t("ocr_region.display_mode"))
        for mode, key in (
            ("overlay", "ocr_display.overlay"),
            ("inline", "ocr_display.inline"),
            ("both", "ocr_display.both"),
        ):
            self.display_actions[mode].setText(t(key))
        self._update_state_text()

    def set_target(self, window: WindowInfo, region: CaptureRegion) -> None:
        self._target = window
        logical = logical_rect_for_region(window, region)
        screen = QGuiApplication.screenAt(logical.center()) or QGuiApplication.primaryScreen()
        available_top = screen.availableGeometry().top() if screen is not None else logical.top()
        self._bar_inside = logical.top() - self.BAR_HEIGHT < available_top
        extra_height = self.BORDER if self._bar_inside else self.BAR_HEIGHT + self.BORDER
        window_top = logical.top() if self._bar_inside else logical.top() - self.BAR_HEIGHT
        self.setMinimumSize(
            self.MIN_CONTENT_WIDTH + self.BORDER * 2,
            self.MIN_CONTENT_HEIGHT + extra_height,
        )
        self._programmatic_geometry = True
        self.setGeometry(
            logical.x() - self.BORDER,
            window_top,
            logical.width() + self.BORDER * 2,
            logical.height() + extra_height,
        )
        self._update_internal_geometry()
        self._programmatic_geometry = False
        self.setWindowTitle(f"VRCTranslate OCR · {window.title}")

    def set_mode(self, mode: str) -> None:
        self._mode = "single" if mode == "single" else "continuous"
        self.single_button.setChecked(self._mode == "single")
        self.continuous_button.setChecked(self._mode == "continuous")

    def set_display_mode(self, mode: str) -> None:
        self._display_mode = mode if mode in {"overlay", "inline", "both"} else "overlay"
        self.display_actions[self._display_mode].setChecked(True)

    def set_state(self, state: str) -> None:
        self._state = state
        if state != "error":
            self._error_message = ""
        self.setProperty("state", state)
        if not self._can_adjust_region():
            self._drag_origin = None
            self._resize_edges = Qt.Edge(0)
            self.setCursor(Qt.CursorShape.ArrowCursor)
        self._update_state_text()
        self.style().unpolish(self)
        self.style().polish(self)

    def set_error(self, message: str) -> None:
        self._error_message = " ".join(message.split()).strip()
        self.set_state("error")

    def _update_state_text(self) -> None:
        if self._state == "error" and self._error_message:
            prefix = self._i18n.tr("ocr_region.state_error") if self._i18n else "错误"
            detail = self._error_message
            summary = detail if len(detail) <= 36 else f"{detail[:35]}…"
            self.state_label.setText(f"{prefix}：{summary}")
            self.state_label.setToolTip(detail)
            return
        self.state_label.setToolTip("")
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
            content = self._content_rect.adjusted(
                -self.BORDER // 2,
                -self.BORDER // 2,
                self.BORDER // 2 - 1,
                self.BORDER // 2 - 1,
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
        self._update_internal_geometry()

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
        if not self._can_adjust_region():
            event.ignore()
            return
        point = event.position().toPoint()
        in_control_bar = self.bar.geometry().contains(point)
        edges = Qt.Edge(0) if in_control_bar else self._edges_at(point)
        # The transparent content is an observation area, not a drag handle.
        # Moving is limited to the control bar; resizing is limited to borders.
        if not edges and not in_control_bar:
            event.ignore()
            return
        self._drag_origin = event.globalPosition().toPoint()
        self._start_geometry = self.geometry()
        self._resize_edges = edges
        self.interaction_started.emit()
        event.accept()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        point = event.position().toPoint()
        if self._drag_origin is None:
            if self.bar.geometry().contains(point):
                self.setCursor(Qt.CursorShape.ArrowCursor)
                return
            self._update_cursor(
                self._edges_at(point) if self._can_adjust_region() else Qt.Edge(0)
            )
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
        content_top_left = self.pos() + self._content_rect.topLeft()
        physical_x, physical_y, ratio = physical_point(content_top_left)
        return CaptureRegion(
            max(0, physical_x - target.left),
            max(0, physical_y - target.top),
            max(1, round(self._content_rect.width() * ratio)),
            max(1, round(self._content_rect.height() * ratio)),
        )

    def _edges_at(self, point: QPoint) -> Qt.Edge:
        margin = 9
        content = self._content_rect
        edges = Qt.Edge(0)
        if abs(point.x() - content.left()) <= margin:
            edges |= Qt.Edge.LeftEdge
        elif abs(point.x() - content.right()) <= margin:
            edges |= Qt.Edge.RightEdge
        if abs(point.y() - content.top()) <= margin:
            edges |= Qt.Edge.TopEdge
        elif abs(point.y() - content.bottom()) <= margin:
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

    def _request_display_mode(self, mode: str) -> None:
        self.set_display_mode(mode)
        self.display_mode_requested.emit(self._display_mode)

    def _update_internal_geometry(self) -> None:
        content_top = 0 if self._bar_inside else self.BAR_HEIGHT
        content_height = max(
            1,
            self.height() - content_top - self.BORDER,
        )
        self._content_rect = QRect(
            self.BORDER,
            content_top,
            max(1, self.width() - self.BORDER * 2),
            content_height,
        )
        self.bar.setGeometry(
            self.BORDER,
            0,
            max(1, self.width() - self.BORDER * 2),
            self.BAR_HEIGHT,
        )

    def _can_adjust_region(self) -> bool:
        return self._state not in {"running", "waiting"}

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
