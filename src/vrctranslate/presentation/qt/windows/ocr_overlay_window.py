from __future__ import annotations

from collections import deque
from uuid import uuid4

from PySide6.QtCore import QEvent, QPoint, QSize, QTimer, Qt, Signal
from PySide6.QtGui import QCloseEvent, QGuiApplication, QMouseEvent
from PySide6.QtWidgets import QFrame, QLabel, QScrollArea, QVBoxLayout, QWidget

from vrctranslate.application.dto import UiSettings
from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.presentation.qt.i18n import I18nManager


class OcrOverlayWindow(QWidget):
    geometry_changed = Signal(int, int, int, int)
    capture_exclusion_failed = Signal()

    def __init__(
        self,
        capture_excluder: WindowCaptureExcluder | None = None,
        i18n: I18nManager | None = None,
    ) -> None:
        super().__init__(None)
        self._capture_excluder = capture_excluder
        self._i18n = i18n
        self._items: deque[tuple[str, str, str]] = deque()
        self._expiry_timers: dict[str, QTimer] = {}
        self._maximum_items = 5
        self._display_seconds = 12.0
        self._show_original = True
        self._capture_warning_emitted = False
        self._allow_close = False
        self._has_saved_position = False
        self._drag_offset: QPoint | None = None
        self._resize_origin: QPoint | None = None
        self._resize_start: QSize | None = None
        self._build_ui()

    def _build_ui(self) -> None:
        self.setObjectName("ocrOverlayWindow")
        title = (
            self._i18n.tr("ocr_overlay.title")
            if self._i18n
            else "VRCTranslate OCR 译文"
        )
        self.setWindowTitle(title)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        self.resize(420, 320)
        self.setMinimumSize(260, 100)
        outer = QVBoxLayout(self)
        outer.setContentsMargins(8, 8, 8, 8)
        self.card = QFrame()
        self.card.setObjectName("ocrOverlayCard")
        self.card.installEventFilter(self)
        self.card.setMouseTracking(True)
        card_layout = QVBoxLayout(self.card)
        card_layout.setContentsMargins(0, 0, 0, 0)
        self.scroll_area = QScrollArea()
        self.scroll_area.setObjectName("ocrScrollArea")
        self.scroll_area.setWidgetResizable(True)
        self.scroll_area.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.scroll_area.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.scroll_area.setFrameShape(QFrame.Shape.NoFrame)
        self.scroll_area.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.scroll_area.viewport().setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.scroll_area.installEventFilter(self)
        self.scroll_area.viewport().installEventFilter(self)
        scroll_content = QWidget()
        scroll_content.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.items_layout = QVBoxLayout(scroll_content)
        self.items_layout.setContentsMargins(14, 12, 14, 12)
        self.items_layout.setSpacing(8)
        self.items_layout.addStretch()
        self.scroll_area.setWidget(scroll_content)
        card_layout.addWidget(self.scroll_area)
        outer.addWidget(self.card)

    def add_translation(self, original: str, translated: str) -> None:
        text = translated.strip()
        if not text:
            return
        item_id = uuid4().hex
        self._items.append((item_id, original.strip(), text))
        while len(self._items) > self._maximum_items:
            removed_id, _, _ = self._items.popleft()
            self._stop_expiry_timer(removed_id)
        timer = QTimer(self)
        timer.setSingleShot(True)
        timer.timeout.connect(lambda current=item_id: self._expire(current))
        timer.start(max(1, int(self._display_seconds * 1000)))
        self._expiry_timers[item_id] = timer
        self._render()

    def clear(self) -> None:
        self._items.clear()
        for timer in self._expiry_timers.values():
            timer.stop()
            timer.deleteLater()
        self._expiry_timers.clear()
        self._render()

    def apply_settings(self, settings: UiSettings) -> None:
        visible = self.isVisible()
        self._maximum_items = settings.ocr_overlay_max_items
        self._display_seconds = settings.ocr_overlay_display_seconds
        self.setWindowOpacity(settings.ocr_overlay_opacity)
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint, settings.ocr_topmost)
        self.setWindowFlag(
            Qt.WindowType.WindowTransparentForInput,
            settings.ocr_mouse_passthrough,
        )
        self.resize(settings.ocr_overlay_width, settings.ocr_overlay_height)
        if settings.ocr_overlay_x >= 0 and settings.ocr_overlay_y >= 0:
            self._has_saved_position = True
            self.move(settings.ocr_overlay_x, settings.ocr_overlay_y)
        self.card.setStyleSheet(f"font-size: {settings.ocr_overlay_font_size}px;")
        while len(self._items) > self._maximum_items:
            removed_id, _, _ = self._items.popleft()
            self._stop_expiry_timer(removed_id)
        self._render()
        if visible:
            self.show()
        self._exclude_from_capture()

    def close_permanently(self) -> None:
        self._allow_close = True
        self.close()

    def _render(self) -> None:
        while self.items_layout.count() > 1:
            item = self.items_layout.takeAt(0)
            widget = item.widget()
            if widget is not None:
                widget.deleteLater()
        for _, original, translated in self._items:
            if self._show_original and original:
                src_label = QLabel(original)
                src_label.setObjectName("ocrOriginal")
                src_label.setWordWrap(True)
                src_label.setTextInteractionFlags(Qt.TextInteractionFlag.NoTextInteraction)
                src_label.installEventFilter(self)
                src_label.setMouseTracking(True)
                self.items_layout.insertWidget(self.items_layout.count() - 1, src_label)
            label = QLabel(translated)
            label.setObjectName("ocrTranslation")
            label.setWordWrap(True)
            label.setTextInteractionFlags(Qt.TextInteractionFlag.NoTextInteraction)
            label.installEventFilter(self)
            label.setMouseTracking(True)
            self.items_layout.insertWidget(self.items_layout.count() - 1, label)

    def _exclude_from_capture(self) -> None:
        if self._capture_excluder is not None and self.isVisible():
            excluded = self._capture_excluder.exclude_from_capture(int(self.winId()))
            if not excluded and not self._capture_warning_emitted:
                self._capture_warning_emitted = True
                self.capture_exclusion_failed.emit()

    def _expire(self, item_id: str) -> None:
        self._items = deque(item for item in self._items if item[0] != item_id)
        self._stop_expiry_timer(item_id)
        self._render()

    def _stop_expiry_timer(self, item_id: str) -> None:
        timer = self._expiry_timers.pop(item_id, None)
        if timer is not None:
            timer.stop()
            timer.deleteLater()

    def showEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().showEvent(event)
        if not self._has_saved_position:
            screen = QGuiApplication.primaryScreen()
            if screen is not None:
                area = screen.availableGeometry()
                self.move(area.right() - self.width() - 32, area.top() + 48)
                self._has_saved_position = True
        self._exclude_from_capture()

    def eventFilter(self, watched, event) -> bool:  # type: ignore[no-untyped-def]
        if isinstance(event, QMouseEvent):
            if event.type() == QEvent.Type.MouseButtonPress:
                self._begin_pointer_action(event)
                return True
            if event.type() == QEvent.Type.MouseMove:
                self._move_pointer_action(event)
                return bool(event.buttons() & Qt.MouseButton.LeftButton)
            if event.type() == QEvent.Type.MouseButtonRelease:
                self._drag_offset = None
                self._resize_origin = None
                self._resize_start = None
                return True
        return super().eventFilter(watched, event)

    def _begin_pointer_action(self, event: QMouseEvent) -> None:
        if event.button() != Qt.MouseButton.LeftButton:
            return
        global_position = event.globalPosition().toPoint()
        local = self.mapFromGlobal(global_position)
        if local.x() >= self.width() - 18 and local.y() >= self.height() - 18:
            self._resize_origin = global_position
            self._resize_start = self.size()
        else:
            self._drag_offset = global_position - self.frameGeometry().topLeft()

    def _move_pointer_action(self, event: QMouseEvent) -> None:
        global_position = event.globalPosition().toPoint()
        if self._resize_origin is not None and self._resize_start is not None:
            delta = global_position - self._resize_origin
            self.resize(self._resize_start.width() + delta.x(), self._resize_start.height() + delta.y())
        elif self._drag_offset is not None:
            self.move(global_position - self._drag_offset)

    def closeEvent(self, event: QCloseEvent) -> None:
        if self._allow_close:
            event.accept()
        else:
            self.hide()
            event.ignore()

    def moveEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().moveEvent(event)
        self.geometry_changed.emit(self.x(), self.y(), self.width(), self.height())

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        self.geometry_changed.emit(self.x(), self.y(), self.width(), self.height())
