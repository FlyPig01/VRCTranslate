from __future__ import annotations

from PySide6.QtCore import QPoint, QSize, Qt, Signal
from PySide6.QtGui import (
    QAction,
    QActionGroup,
    QContextMenuEvent,
    QGuiApplication,
    QMouseEvent,
)
from PySide6.QtWidgets import QMenu, QToolButton, QVBoxLayout, QWidget

from vrctranslate.application.dto import UiSettings
from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon


class OcrOrbWindow(QWidget):
    toggle_requested = Signal()
    single_requested = Signal()
    continuous_requested = Signal()
    region_requested = Signal()
    region_visibility_requested = Signal()
    display_mode_requested = Signal(str)
    exit_requested = Signal()
    geometry_changed = Signal(int, int)

    def __init__(
        self,
        capture_excluder: WindowCaptureExcluder | None = None,
        i18n: I18nManager | None = None,
    ) -> None:
        super().__init__(None)
        self._capture_excluder = capture_excluder
        self._i18n = i18n
        self._state = "idle"
        self._drag_start: QPoint | None = None
        self._window_start = QPoint()
        self._moved = False
        self._allow_close = False
        self._has_saved_position = False
        self._build_ui()
        if i18n is not None:
            i18n.language_changed.connect(lambda *_: self._retranslate())
        self._retranslate()

    def _build_ui(self) -> None:
        self.setObjectName("ocrOrbWindow")
        self.setWindowTitle("VRCTranslate OCR")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        self.setFixedSize(58, 58)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(5, 4, 5, 6)
        self.button = QToolButton()
        self.button.setObjectName("ocrOrbButton")
        self.button.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.button.setIconSize(QSize(48, 48))
        layout.addWidget(self.button)
        self._build_menu()
        self.set_state("idle")

    def _build_menu(self) -> None:
        self.menu = QMenu(self)
        self.region_action = QAction(self)
        self.single_action = QAction(self)
        self.continuous_action = QAction(self)
        self.display_menu = QMenu(self)
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
        self.visibility_action = QAction(self)
        self.pause_action = QAction(self)
        self.exit_action = QAction(self)
        self.region_action.triggered.connect(self.region_requested)
        self.single_action.triggered.connect(self.single_requested)
        self.continuous_action.triggered.connect(self.continuous_requested)
        self.visibility_action.triggered.connect(self.region_visibility_requested)
        self.pause_action.triggered.connect(self.toggle_requested)
        self.exit_action.triggered.connect(self.exit_requested)
        self.menu.addAction(self.region_action)
        self.menu.addSeparator()
        self.menu.addAction(self.single_action)
        self.menu.addAction(self.continuous_action)
        self.menu.addMenu(self.display_menu)
        self.menu.addAction(self.pause_action)
        self.menu.addSeparator()
        self.menu.addAction(self.visibility_action)
        self.menu.addAction(self.exit_action)
        self.set_display_mode("overlay")

    def _retranslate(self) -> None:
        if self._i18n is None:
            return
        t = self._i18n.tr
        self.region_action.setText(t("ocr_orb.select_region"))
        self.single_action.setText(t("ocr_orb.single"))
        self.continuous_action.setText(t("ocr_orb.continuous"))
        self.display_menu.setTitle(t("ocr_orb.display_mode"))
        for mode, key in (
            ("overlay", "ocr_display.overlay"),
            ("inline", "ocr_display.inline"),
            ("both", "ocr_display.both"),
        ):
            self.display_actions[mode].setText(t(key))
        self.pause_action.setText(t("ocr_orb.toggle"))
        self.visibility_action.setText(t("ocr_orb.toggle_region"))
        self.exit_action.setText(t("ocr_orb.exit"))
        self.setToolTip(t("ocr_orb.tooltip"))

    def set_state(self, state: str) -> None:
        if state not in {"idle", "running", "waiting", "error"}:
            state = "idle"
        self._state = state
        self.button.setIcon(load_icon(f"ui/ocr_orb_{state}.svg"))
        self.setProperty("state", state)
        self.style().unpolish(self)
        self.style().polish(self)

    def set_display_mode(self, mode: str) -> None:
        normalized = mode if mode in {"overlay", "inline", "both"} else "overlay"
        self.display_actions[normalized].setChecked(True)

    def apply_settings(self, settings: UiSettings) -> None:
        visible = self.isVisible()
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint, settings.ocr_orb_topmost)
        if settings.ocr_orb_x >= 0 and settings.ocr_orb_y >= 0:
            self.move(settings.ocr_orb_x, settings.ocr_orb_y)
            self._has_saved_position = True
        if visible:
            self.show()
        self._exclude_from_capture()

    def show_and_raise(self) -> None:
        self.show()
        self.raise_()
        self._exclude_from_capture()

    def close_permanently(self) -> None:
        self._allow_close = True
        self.close()

    def contextMenuEvent(self, event: QContextMenuEvent) -> None:
        self.menu.popup(event.globalPos())
        event.accept()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._drag_start = event.globalPosition().toPoint()
            self._window_start = self.pos()
            self._moved = False
        event.accept()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_start is None or not event.buttons() & Qt.MouseButton.LeftButton:
            return
        delta = event.globalPosition().toPoint() - self._drag_start
        if delta.manhattanLength() >= 4:
            self._moved = True
        self.move(self._window_start + delta)
        event.accept()

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self._drag_start is not None:
            if self._moved:
                self.geometry_changed.emit(self.x(), self.y())
            else:
                self.toggle_requested.emit()
            self._drag_start = None
            event.accept()

    def showEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().showEvent(event)
        if not self._has_saved_position:
            screen = QGuiApplication.primaryScreen()
            if screen is not None:
                area = screen.availableGeometry()
                self.move(area.right() - self.width() - 28, area.center().y())
                self._has_saved_position = True
        self._exclude_from_capture()

    def closeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        if self._allow_close:
            event.accept()
        else:
            self.hide()
            event.ignore()

    def _exclude_from_capture(self) -> None:
        if self._capture_excluder is not None and self.isVisible():
            self._capture_excluder.exclude_from_capture(int(self.winId()))

    def _request_display_mode(self, mode: str) -> None:
        self.set_display_mode(mode)
        self.display_mode_requested.emit(mode)
