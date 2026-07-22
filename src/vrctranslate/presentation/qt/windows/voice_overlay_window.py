from __future__ import annotations

from collections import deque

from PySide6.QtCore import (
    QEasingCurve,
    QPoint,
    QPropertyAnimation,
    QRect,
    QSize,
    QTimer,
    Qt,
    Signal,
)
from PySide6.QtGui import QCloseEvent, QGuiApplication, QMouseEvent
from PySide6.QtWidgets import (
    QFrame,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import VoiceOverlaySettings
from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.domain.speech import VoiceCaption
from vrctranslate.presentation.qt.font_utils import font_with_pixel_height
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.windows.ocr_overlay.interaction import (
    OverlaySizeGrip,
)


class VoiceOverlayWindow(QWidget):
    geometry_changed = Signal(int, int, int, int)
    recognition_toggle_requested = Signal()

    def __init__(
        self,
        capture_excluder: WindowCaptureExcluder | None = None,
        i18n: I18nManager | None = None,
    ) -> None:
        super().__init__(None)
        self._capture_excluder = capture_excluder
        self._i18n = i18n
        self._allow_close = False
        self._drag_offset: QPoint | None = None
        self._drag_window_start = QPoint()
        self._moved = False
        self._collapsed = True
        self._recognition_running = False
        self._recognition_state = "idle"
        self._animating_geometry = False
        self._transition_target = ""
        self._orb_position: QPoint | None = None
        self._settings = VoiceOverlaySettings()
        self._expanded_size = QSize(
            self._settings.width,
            self._settings.height,
        )
        self._captions: deque[VoiceCaption] = deque()
        self._live_caption: VoiceCaption | None = None
        self._latest_caption_item: QWidget | None = None
        self._has_saved_position = False
        self._start_recognition_icon = load_icon("ui/action_play.svg")
        self._starting_recognition_icon = load_icon("ui/action_loading.svg")
        self._pause_recognition_icon = load_icon("ui/action_pause.svg")
        self._build_ui()
        if i18n is not None:
            i18n.language_changed.connect(lambda _: self._retranslate())

    def _build_ui(self) -> None:
        self.setObjectName("voiceOverlayWindow")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        self._outer = QVBoxLayout(self)
        self._outer.setContentsMargins(5, 4, 5, 6)
        self._outer.setSpacing(0)
        self.orb_button = QToolButton()
        self.orb_button.setObjectName("voiceOrbButton")
        self.orb_button.setAttribute(
            Qt.WidgetAttribute.WA_TransparentForMouseEvents
        )
        self.orb_button.setIcon(load_icon("ui/voice_orb.svg"))
        self.orb_button.setIconSize(QSize(48, 48))
        self._outer.addWidget(self.orb_button)

        self.surface = QFrame()
        self.surface.setObjectName("voiceOverlaySurface")
        surface_layout = QVBoxLayout(self.surface)
        surface_layout.setContentsMargins(16, 10, 16, 14)
        surface_layout.setSpacing(7)
        self.header = QWidget()
        self.header.setObjectName("voiceOverlayHeader")
        header_layout = QHBoxLayout(self.header)
        header_layout.setContentsMargins(1, 0, 0, 0)
        header_layout.setSpacing(8)
        self.title_label = QLabel()
        self.title_label.setObjectName("voiceOverlayTitle")
        self.title_label.setAttribute(
            Qt.WidgetAttribute.WA_TransparentForMouseEvents
        )
        self.collapse_button = QToolButton()
        self.collapse_button.setObjectName("voiceOverlayCollapseButton")
        self.collapse_button.setIcon(load_icon("ui/action_collapse.svg"))
        self.collapse_button.setIconSize(QSize(18, 18))
        self.recognition_button = QPushButton()
        self.recognition_button.setObjectName("voiceRecognitionToggleButton")
        self.recognition_button.setIconSize(QSize(18, 18))
        header_layout.addWidget(self.title_label)
        header_layout.addStretch()
        header_layout.addWidget(self.recognition_button)
        header_layout.addWidget(self.collapse_button)
        surface_layout.addWidget(self.header)

        self.error_label = QLabel()
        self.error_label.setObjectName("voiceOverlayError")
        self.error_label.setWordWrap(True)
        self.error_label.hide()
        surface_layout.addWidget(self.error_label)

        self.caption_container = QWidget()
        self.caption_container.setObjectName("voiceCaptionContainer")
        self._layout = QVBoxLayout(self.caption_container)
        self._layout.setContentsMargins(0, 0, 0, 0)
        self._layout.setSpacing(8)
        self._empty = QLabel()
        self._empty.setObjectName("voiceOverlayEmpty")
        self._empty.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self._layout.addWidget(self._empty, 1)
        self.caption_scroll = QScrollArea()
        self.caption_scroll.setObjectName("voiceCaptionScroll")
        self.caption_scroll.setFrameShape(QFrame.Shape.NoFrame)
        self.caption_scroll.setWidgetResizable(True)
        self.caption_scroll.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        self.caption_scroll.setVerticalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        self.caption_scroll.setMinimumSize(0, 0)
        self.caption_scroll.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Ignored,
        )
        self.caption_scroll.setWidget(self.caption_container)
        surface_layout.addWidget(self.caption_scroll, 1)
        resize_row = QHBoxLayout()
        resize_row.setContentsMargins(0, 0, 0, 0)
        resize_row.addStretch()
        self.size_grip = OverlaySizeGrip(self.surface)
        self.size_grip.setObjectName("voiceOverlaySizeGrip")
        self.size_grip.setFixedSize(18, 18)
        resize_row.addWidget(self.size_grip)
        surface_layout.addLayout(resize_row)
        self._caption_scroll_timer = QTimer(self)
        self._caption_scroll_timer.setSingleShot(True)
        self._caption_scroll_timer.setInterval(0)
        self._caption_scroll_timer.timeout.connect(self._scroll_to_latest_caption)
        self._caption_scroll_settle_timer = QTimer(self)
        self._caption_scroll_settle_timer.setSingleShot(True)
        self._caption_scroll_settle_timer.setInterval(40)
        self._caption_scroll_settle_timer.timeout.connect(
            self._scroll_to_latest_caption
        )
        self.caption_scroll.verticalScrollBar().rangeChanged.connect(
            self._caption_scroll_range_changed
        )
        self._outer.addWidget(self.surface)

        self._geometry_animation = QPropertyAnimation(
            self,
            b"geometry",
            self,
        )
        self._geometry_animation.setDuration(190)
        self._geometry_animation.finished.connect(self._transition_finished)
        self.recognition_button.clicked.connect(
            lambda: self.recognition_toggle_requested.emit()
        )
        self.collapse_button.clicked.connect(self.collapse_to_orb)
        self.surface.hide()
        self.setFixedSize(58, 58)
        self._retranslate()

    def _retranslate(self) -> None:
        self.setWindowTitle(
            self._i18n.tr("voice_overlay.title")
            if self._i18n is not None
            else "语音翻译字幕"
        )
        self.title_label.setText(
            self._i18n.tr("voice_overlay.caption_title")
            if self._i18n is not None
            else "语音字幕"
        )
        self.collapse_button.setToolTip(
            self._i18n.tr("voice_overlay.collapse")
            if self._i18n is not None
            else "折叠为悬浮球"
        )
        self.size_grip.setToolTip(
            self._i18n.tr("voice_overlay.resize")
            if self._i18n is not None
            else "拖动调整字幕浮窗大小"
        )
        self._refresh_recognition_button()
        self.setToolTip(
            self._i18n.tr("voice_overlay.orb_tooltip")
            if self._i18n is not None
            else "左键展开语音字幕；拖动可移动悬浮球"
        )
        self._empty.setText(
            self._i18n.tr("voice_overlay.empty")
            if self._i18n is not None
            else "等待其他程序播放语音…"
        )

    def apply_settings(self, settings: VoiceOverlaySettings) -> None:
        self._finish_transition_immediately()
        self._settings = settings
        self._expanded_size = QSize(
            max(320, settings.width),
            max(140, settings.height),
        )
        visible = self.isVisible()
        topmost_changed = self._set_window_flag(
            Qt.WindowType.WindowStaysOnTopHint,
            settings.topmost,
        )
        self.setWindowOpacity(1.0 if self._collapsed else settings.opacity)
        if not self._collapsed:
            self.resize(self._expanded_size)
        if settings.x >= 0 and settings.y >= 0:
            self.move(settings.x, settings.y)
            if self._collapsed:
                self._orb_position = QPoint(settings.x, settings.y)
            self._has_saved_position = True
        while len(self._captions) > settings.max_items:
            self._captions.popleft()
        self._rebuild_items()
        if visible and topmost_changed:
            self.show()
        self._exclude_from_capture()

    def _set_window_flag(self, flag: Qt.WindowType, enabled: bool) -> bool:
        if bool(self.windowFlags() & flag) == enabled:
            return False
        self.setWindowFlag(flag, enabled)
        return True

    def add_caption(self, caption: VoiceCaption) -> None:
        self._live_caption = None
        self._captions.append(caption)
        while len(self._captions) > self._settings.max_items:
            self._captions.popleft()
        self._rebuild_items()

    def clear_captions(self) -> None:
        self._captions.clear()
        self._live_caption = None
        self._rebuild_items()

    def show_error(self, message: str) -> None:
        self.error_label.setText(message)
        self.error_label.setVisible(bool(message))
        if message:
            self.expand_from_orb()

    def clear_error(self) -> None:
        self.error_label.clear()
        self.error_label.hide()

    def set_live_caption(self, original: str, translated: str = "") -> None:
        self._live_caption = (
            VoiceCaption(-1, original, translated)
            if original or translated
            else None
        )
        self._rebuild_items()

    def show_overlay(self) -> None:
        self.expand_from_orb()

    def set_recognition_running(self, running: bool) -> None:
        self._recognition_running = running
        self.set_recognition_state("running" if running else "idle")
        if running:
            self.expand_from_orb()
        else:
            self.collapse_to_orb()

    def set_recognition_state(self, state: str) -> None:
        self._recognition_state = (
            state if state in {"idle", "starting", "running"} else "idle"
        )
        self._recognition_running = self._recognition_state == "running"
        self._refresh_recognition_button()

    def _refresh_recognition_button(self) -> None:
        if not hasattr(self, "recognition_button"):
            return
        keys = {
            "idle": "voice_overlay.start_recognition",
            "starting": "voice_overlay.starting_recognition",
            "running": "voice_overlay.stop_recognition",
        }
        fallbacks = {
            "idle": "开始识别",
            "starting": "正在启动…",
            "running": "停止识别",
        }
        label = (
            self._i18n.tr(keys[self._recognition_state])
            if self._i18n is not None
            else fallbacks[self._recognition_state]
        )
        self.recognition_button.setText("")
        self.recognition_button.setToolTip(label)
        self.recognition_button.setAccessibleName(label)
        self.recognition_button.setIcon(
            self._pause_recognition_icon
            if self._recognition_state == "running"
            else self._starting_recognition_icon
            if self._recognition_state == "starting"
            else self._start_recognition_icon
        )
        self.recognition_button.setEnabled(
            self._recognition_state != "starting"
        )
        self.recognition_button.setProperty(
            "state",
            self._recognition_state,
        )
        self.recognition_button.style().unpolish(self.recognition_button)
        self.recognition_button.style().polish(self.recognition_button)

    def is_collapsed(self) -> bool:
        return self._collapsed

    def collapse_to_orb(self) -> None:
        if self._recognition_state in {"starting", "running"}:
            self.recognition_toggle_requested.emit()
            return
        if self._collapsed and not self._animating_geometry:
            if self._orb_position is not None:
                self.move(self._orb_position)
            self.show()
            self.raise_()
            self._exclude_from_capture()
            self._emit_geometry_changed()
            return
        target_position = self._orb_position or self.pos()
        # Remove the expanded layout's minimum-size constraint before the
        # geometry animation. Keeping visible caption widgets here makes
        # Windows reject the shrinking intermediate sizes on high DPI.
        self.surface.hide()
        self.orb_button.show()
        self._outer.setContentsMargins(5, 4, 5, 6)
        self.setMinimumSize(0, 0)
        self.setMaximumSize(16777215, 16777215)
        self._start_geometry_transition(
            "collapsed",
            QRect(target_position, QSize(58, 58)),
        )

    def expand_from_orb(self) -> None:
        if not self._collapsed and not self._animating_geometry:
            self.show()
            self.raise_()
            self._exclude_from_capture()
            return
        if self._collapsed:
            self._orb_position = QPoint(self.pos())
        target = self._expanded_target_geometry()
        self._animating_geometry = True
        # Keep only the compact orb visible until the top-level window has
        # reached a geometry that can contain the expanded layout.
        self.surface.hide()
        self.orb_button.show()
        self._outer.setContentsMargins(5, 4, 5, 6)
        self.setMinimumSize(0, 0)
        self.setMaximumSize(16777215, 16777215)
        self.setWindowOpacity(self._settings.opacity)
        self.show()
        self.raise_()
        self._start_geometry_transition("expanded", target)
        self._exclude_from_capture()

    def reset_geometry(self, settings: VoiceOverlaySettings) -> None:
        self._has_saved_position = False
        self._expanded_size = QSize(
            max(320, settings.width),
            max(140, settings.height),
        )
        if not self._collapsed:
            self.resize(self._expanded_size)
        screen = QGuiApplication.primaryScreen()
        if screen is not None:
            area = screen.availableGeometry()
            self.move(
                area.center().x() - self._expanded_size.width() // 2,
                area.bottom() - self._expanded_size.height() - 80,
            )
            self._has_saved_position = True

    def close_permanently(self) -> None:
        self._allow_close = True
        self.close()

    def _rebuild_items(self) -> None:
        self._latest_caption_item = None
        while self._layout.count() > 1:
            item = self._layout.takeAt(1)
            widget = item.widget()
            if widget is not None:
                # Reparenting a visible caption to None temporarily turns it
                # into a native top-level QWidget. On Windows that produces a
                # one-frame blank window titled "VRCTranslate".
                widget.hide()
                widget.setObjectName("")
                for child in widget.findChildren(QWidget):
                    child.setObjectName("")
                widget.deleteLater()
        captions = list(self._captions)
        if self._live_caption is not None:
            captions.append(self._live_caption)
        mode = self._settings.display_mode
        if mode not in {"translation", "original", "both"}:
            mode = "both" if self._settings.show_original else "translation"
        show_original = mode in {"original", "both"}
        show_translation = mode in {"translation", "both"}
        visible_captions = [
            caption
            for caption in captions
            if (show_original and caption.original)
            or (show_translation and caption.translated)
        ]
        self._empty.setVisible(not visible_captions)
        for caption in visible_captions:
            item = QFrame()
            item.setObjectName("voiceCaptionItem")
            layout = QVBoxLayout(item)
            layout.setContentsMargins(10, 7, 10, 8)
            layout.setSpacing(2)
            if show_original and caption.original:
                original = QLabel(caption.original)
                original.setObjectName("voiceCaptionOriginal")
                original.setWordWrap(True)
                original.setFont(
                    font_with_pixel_height(
                        original,
                        original.font(),
                        self._settings.font_size
                        if mode == "original"
                        else max(10, round(self._settings.font_size * 0.76)),
                    )
                )
                layout.addWidget(original)
            if show_translation and caption.translated:
                translated = QLabel(caption.translated)
                translated.setObjectName("voiceCaptionTranslation")
                translated.setWordWrap(True)
                translated.setFont(
                    font_with_pixel_height(
                        translated,
                        translated.font(),
                        self._settings.font_size,
                    )
                )
                layout.addWidget(translated)
            self._layout.addWidget(item)
            self._latest_caption_item = item
        self._layout.addStretch(1)
        self._caption_scroll_timer.start()
        self._caption_scroll_settle_timer.start()

    def _scroll_to_latest_caption(self) -> None:
        self._layout.activate()
        self.caption_container.adjustSize()
        if self._latest_caption_item is not None:
            self.caption_scroll.ensureWidgetVisible(
                self._latest_caption_item,
                0,
                0,
            )
        bar = self.caption_scroll.verticalScrollBar()
        bar.setValue(bar.maximum())

    def _caption_scroll_range_changed(
        self,
        _minimum: int,
        maximum: int,
    ) -> None:
        self.caption_scroll.verticalScrollBar().setValue(maximum)

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
                    area.center().x() - self._expanded_size.width() // 2,
                    area.bottom() - self._expanded_size.height() - 80,
                )
                if self._collapsed:
                    self._orb_position = QPoint(self.pos())
                self._has_saved_position = True
        self._exclude_from_capture()

    def closeEvent(self, event: QCloseEvent) -> None:
        if self._allow_close:
            event.accept()
        else:
            self.hide()
            event.ignore()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._drag_offset = (
                event.globalPosition().toPoint() - self.frameGeometry().topLeft()
            )
            self._drag_window_start = self.pos()
            self._moved = False
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_offset is not None and event.buttons() & Qt.MouseButton.LeftButton:
            next_position = event.globalPosition().toPoint() - self._drag_offset
            if (next_position - self._drag_window_start).manhattanLength() >= 4:
                self._moved = True
            self.move(next_position)
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        should_expand = (
            event.button() == Qt.MouseButton.LeftButton
            and self._drag_offset is not None
            and self._collapsed
            and not self._moved
        )
        self._drag_offset = None
        super().mouseReleaseEvent(event)
        if should_expand:
            self.expand_from_orb()

    def moveEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().moveEvent(event)
        if self._collapsed and not self._animating_geometry:
            self._orb_position = QPoint(self.pos())
        self._emit_geometry_changed()

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        if not self._collapsed and not self._animating_geometry:
            self._expanded_size = event.size().expandedTo(QSize(320, 140))
        self._emit_geometry_changed()

    def _emit_geometry_changed(self) -> None:
        if self._animating_geometry:
            return
        self.geometry_changed.emit(
            self.x(),
            self.y(),
            self._expanded_size.width(),
            self._expanded_size.height(),
        )

    def _expanded_target_geometry(self) -> QRect:
        anchor = self._orb_position or self.pos()
        screen = QGuiApplication.screenAt(anchor)
        if screen is None:
            screen = QGuiApplication.primaryScreen()
        if screen is None:
            return QRect(anchor, self._expanded_size)
        area = screen.availableGeometry()
        maximum_x = max(
            area.left(),
            area.right() - self._expanded_size.width() + 1,
        )
        maximum_y = max(
            area.top(),
            area.bottom() - self._expanded_size.height() + 1,
        )
        return QRect(
            QPoint(
                min(max(anchor.x(), area.left()), maximum_x),
                min(max(anchor.y(), area.top()), maximum_y),
            ),
            self._expanded_size,
        )

    def _start_geometry_transition(self, target: str, end: QRect) -> None:
        self._geometry_animation.stop()
        self._animating_geometry = True
        self._transition_target = target
        self._geometry_animation.setEasingCurve(
            QEasingCurve.Type.OutCubic
            if target == "expanded"
            else QEasingCurve.Type.InCubic
        )
        self._geometry_animation.setStartValue(self.geometry())
        self._geometry_animation.setEndValue(end)
        self._geometry_animation.start()

    def _finish_transition_immediately(self) -> None:
        if not self._animating_geometry:
            return
        end = self._geometry_animation.endValue()
        self._geometry_animation.stop()
        if isinstance(end, QRect):
            self.setGeometry(end)
        self._transition_finished()

    def _transition_finished(self) -> None:
        target = self._transition_target
        if not target:
            return
        self._transition_target = ""
        if target == "collapsed":
            self._collapsed = True
            self.surface.hide()
            self.orb_button.show()
            self._outer.setContentsMargins(5, 4, 5, 6)
            self.setWindowOpacity(1.0)
            self.setFixedSize(58, 58)
            if self._orb_position is not None:
                self.move(self._orb_position)
        else:
            self._collapsed = False
            self.orb_button.hide()
            self.surface.show()
            self._outer.setContentsMargins(8, 8, 8, 8)
            self.setMinimumSize(320, 140)
            self.setMaximumSize(16777215, 16777215)
            self.setWindowOpacity(self._settings.opacity)
        self._animating_geometry = False
        self._exclude_from_capture()
        self._emit_geometry_changed()
