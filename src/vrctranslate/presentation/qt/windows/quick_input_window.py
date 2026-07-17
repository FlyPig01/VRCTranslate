from __future__ import annotations

from PySide6.QtCore import QPoint, Qt, Signal
from PySide6.QtGui import QCloseEvent, QGuiApplication, QHideEvent, QMouseEvent
from PySide6.QtWidgets import QFrame, QLabel, QLineEdit, QVBoxLayout, QWidget

from vrctranslate.application.dto import UiSettings
from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.presentation.qt.i18n import I18nManager


class QuickInputWindow(QWidget):
    submitted = Signal(str)
    text_activity = Signal(str)
    hidden_by_user = Signal()
    geometry_changed = Signal(int, int, int)

    def __init__(
        self,
        capture_excluder: WindowCaptureExcluder | None = None,
        i18n: I18nManager | None = None,
    ) -> None:
        super().__init__(None)
        self._capture_excluder = capture_excluder
        self._i18n = i18n
        self._drag_offset: QPoint | None = None
        self._allow_close = False
        self._has_saved_position = False
        self._build_ui()
        if i18n is not None:
            i18n.language_changed.connect(lambda _: self._retranslate())

    def _build_ui(self) -> None:
        self.setObjectName("quickInputWindow")
        self.setWindowTitle("VRCTranslate 快捷输入")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        self.resize(480, 70)
        outer = QVBoxLayout(self)
        outer.setContentsMargins(8, 8, 8, 8)
        outer.setSpacing(0)
        self.card = QFrame()
        self.card.setObjectName("overlayCard")
        card_layout = QVBoxLayout(self.card)
        card_layout.setContentsMargins(10, 8, 10, 7)
        card_layout.setSpacing(2)
        self.input = QLineEdit()
        self.input.setObjectName("quickInput")
        self.input.setClearButtonEnabled(False)
        self.status = QLabel("")
        self.status.setObjectName("quickStatus")
        self.status.setFixedHeight(14)
        card_layout.addWidget(self.input)
        card_layout.addWidget(self.status)
        outer.addWidget(self.card)
        self.input.returnPressed.connect(self._submit)
        self.input.textChanged.connect(self.text_activity)
        self._retranslate()

    def _retranslate(self) -> None:
        if self._i18n is not None:
            self.input.setPlaceholderText(self._i18n.tr("quick_input.placeholder"))

    @property
    def text(self) -> str:
        return self.input.text()

    def take_text(self) -> str:
        text = self.input.text().strip()
        self.input.clear()
        return text

    def restore_text(self, text: str) -> None:
        current = self.input.text().strip()
        self.input.setText(text if not current else f"{text} {current}")
        self.input.setFocus()
        self.input.setCursorPosition(len(self.input.text()))

    def set_state(self, state: str, message: str = "") -> None:
        self.setProperty("state", state)
        self.status.setText(message)
        self.style().unpolish(self)
        self.style().polish(self)

    def apply_settings(self, settings: UiSettings) -> None:
        visible = self.isVisible()
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint, settings.input_topmost)
        self.resize(settings.input_width, self.height())
        if settings.input_x >= 0 and settings.input_y >= 0:
            self._has_saved_position = True
            self.move(settings.input_x, settings.input_y)
        if visible:
            self.show()
        self._exclude_from_capture()

    def show_and_focus(self) -> None:
        self.show()
        self.raise_()
        self.activateWindow()
        self.input.setFocus()
        self._exclude_from_capture()

    def close_permanently(self) -> None:
        self._allow_close = True
        self.close()

    def _submit(self) -> None:
        text = self.input.text().strip()
        if text:
            self.submitted.emit(text)

    def _exclude_from_capture(self) -> None:
        if self._capture_excluder is not None and self.isVisible():
            self._capture_excluder.exclude_from_capture(int(self.winId()))

    def showEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().showEvent(event)
        if not self._has_saved_position:
            screen = QGuiApplication.primaryScreen()
            if screen is not None:
                area = screen.availableGeometry()
                self.move(
                    area.center().x() - self.width() // 2,
                    area.bottom() - self.height() - 70,
                )
                self._has_saved_position = True
        self._exclude_from_capture()

    def hideEvent(self, event: QHideEvent) -> None:
        super().hideEvent(event)
        self.hidden_by_user.emit()

    def closeEvent(self, event: QCloseEvent) -> None:
        if self._allow_close:
            event.accept()
        else:
            self.hide()
            event.ignore()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._drag_offset = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_offset is not None and event.buttons() & Qt.MouseButton.LeftButton:
            self.move(event.globalPosition().toPoint() - self._drag_offset)
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        self._drag_offset = None
        super().mouseReleaseEvent(event)

    def moveEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().moveEvent(event)
        self.geometry_changed.emit(self.x(), self.y(), self.width())

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        self.geometry_changed.emit(self.x(), self.y(), self.width())
