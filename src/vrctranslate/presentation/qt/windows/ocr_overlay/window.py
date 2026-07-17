from __future__ import annotations

from collections import deque

from PySide6.QtCore import QTimer, Qt, Signal
from PySide6.QtGui import QCloseEvent, QGuiApplication, QHideEvent, QMoveEvent, QResizeEvent
from PySide6.QtWidgets import (
    QFrame,
    QHBoxLayout,
    QScrollArea,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import UiSettings
from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.windows.ocr_overlay.content_model import (
    OverlayContentModel,
)
from vrctranslate.presentation.qt.windows.ocr_overlay.interaction import (
    OverlayDragHandle,
    OverlaySizeGrip,
)
from vrctranslate.presentation.qt.windows.ocr_overlay.surface import OverlaySurface
from vrctranslate.presentation.qt.windows.ocr_overlay.translation_item import (
    TranslationItem,
)


class OcrOverlayWindow(QWidget):
    geometry_changed = Signal(int, int, int, int)
    capture_exclusion_failed = Signal()
    hidden_by_user = Signal()

    def __init__(
        self,
        capture_excluder: WindowCaptureExcluder | None = None,
        i18n: I18nManager | None = None,
    ) -> None:
        super().__init__(None)
        self._capture_excluder = capture_excluder
        self._i18n = i18n
        self._capture_warning_emitted = False
        self._allow_close = False
        self._has_saved_position = False
        self._auto_scroll = True
        self._interaction_active = False
        self._render_scheduled = False
        self._entry_widgets: dict[str, TranslationItem] = {}
        self._font_size = 16
        self._show_original = True
        self._mouse_passthrough = False
        self._model = OverlayContentModel(self)
        self._build_ui()
        self._model.changed.connect(self._schedule_render)
        if i18n is not None:
            i18n.language_changed.connect(lambda _: self._retranslate())
        self._retranslate()

    def _build_ui(self) -> None:
        self.setObjectName("ocrOverlayWindow")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        self.setMinimumSize(280, 140)
        self.resize(420, 320)
        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)
        self.surface = OverlaySurface()
        self.card = self.surface  # Compatibility handle for lightweight UI tests.
        surface_layout = QVBoxLayout(self.surface)
        surface_layout.setContentsMargins(8, 6, 6, 6)
        surface_layout.setSpacing(2)
        self.drag_handle = OverlayDragHandle()
        self.drag_handle.interaction_started.connect(self._begin_interaction)
        self.drag_handle.interaction_finished.connect(self._finish_interaction)
        surface_layout.addWidget(self.drag_handle)
        self.scroll_area = QScrollArea()
        self.scroll_area.setObjectName("ocrScrollArea")
        self.scroll_area.setWidgetResizable(True)
        self.scroll_area.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        self.scroll_area.setVerticalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAsNeeded
        )
        self.scroll_area.setFrameShape(QFrame.Shape.NoFrame)
        self.scroll_area.verticalScrollBar().valueChanged.connect(
            self._on_scroll_changed
        )
        self._content = QWidget()
        self._content.setObjectName("ocrOverlayContent")
        self._items_layout = QVBoxLayout(self._content)
        self._items_layout.setContentsMargins(2, 2, 4, 2)
        self._items_layout.setSpacing(6)
        self._items_layout.addStretch()
        self.scroll_area.setWidget(self._content)
        surface_layout.addWidget(self.scroll_area, 1)
        footer = QHBoxLayout()
        footer.setContentsMargins(0, 0, 0, 0)
        footer.addStretch()
        self.size_grip = OverlaySizeGrip(self.surface)
        self.size_grip.setObjectName("ocrOverlaySizeGrip")
        self.size_grip.interaction_started.connect(self._begin_interaction)
        self.size_grip.interaction_finished.connect(self._finish_interaction)
        footer.addWidget(self.size_grip)
        surface_layout.addLayout(footer)
        outer.addWidget(self.surface)
        self._render_timer = QTimer(self)
        self._render_timer.setSingleShot(True)
        self._render_timer.setInterval(0)
        self._render_timer.timeout.connect(self._render_entries)
        self._geometry_commit_timer = QTimer(self)
        self._geometry_commit_timer.setSingleShot(True)
        self._geometry_commit_timer.setInterval(850)
        self._geometry_commit_timer.timeout.connect(self._emit_geometry)

    def _retranslate(self) -> None:
        title = (
            self._i18n.tr("ocr_overlay.title")
            if self._i18n is not None
            else "VRCTranslate OCR 译文"
        )
        handle = (
            self._i18n.tr("ocr_overlay.drag_handle")
            if self._i18n is not None
            else "OCR 译文"
        )
        self.setWindowTitle(title)
        self.drag_handle.set_label(handle)

    @property
    def _items(self):  # type: ignore[no-untyped-def]
        return deque(
            (entry.entry_id, entry.original, entry.translated)
            for entry in self._model.entries
        )

    @property
    def _display_seconds(self) -> float:
        return self._model.display_seconds

    @_display_seconds.setter
    def _display_seconds(self, value: float) -> None:
        self._model.configure(len(self._model.entries) or 5, value)

    def add_translation(self, original: str, translated: str) -> None:
        self._model.add(original, translated)

    def clear(self) -> None:
        self._model.clear()

    def apply_settings(self, settings: UiSettings) -> None:
        visible = self.isVisible()
        self._model.configure(
            settings.ocr_overlay_max_items,
            settings.ocr_overlay_display_seconds,
        )
        self._font_size = settings.ocr_overlay_font_size
        self._show_original = getattr(settings, "ocr_overlay_show_original", True)
        self.surface.set_background_opacity(settings.ocr_overlay_opacity)
        self._set_window_flag(
            Qt.WindowType.WindowStaysOnTopHint, settings.ocr_topmost
        )
        self._mouse_passthrough = settings.ocr_mouse_passthrough
        self._set_window_flag(
            Qt.WindowType.WindowTransparentForInput,
            self._mouse_passthrough,
        )
        self.drag_handle.setVisible(not self._mouse_passthrough)
        self.size_grip.setVisible(not self._mouse_passthrough)
        self.resize(settings.ocr_overlay_width, settings.ocr_overlay_height)
        if settings.ocr_overlay_x >= 0 and settings.ocr_overlay_y >= 0:
            self._has_saved_position = True
            self.move(settings.ocr_overlay_x, settings.ocr_overlay_y)
        self._schedule_render()
        if visible:
            self.show()
        self._exclude_from_capture()

    def apply_visual_preview(
        self, background_opacity: float, font_size: int, show_original: bool
    ) -> None:
        self.surface.set_background_opacity(background_opacity)
        self._font_size = font_size
        self._show_original = show_original
        self._schedule_render()

    def reset_geometry(self, width: int = 420, height: int = 220) -> None:
        self.resize(width, height)
        screen = QGuiApplication.primaryScreen()
        if screen is not None:
            area = screen.availableGeometry()
            self.move(area.right() - width - 32, area.top() + 48)
        self._has_saved_position = True

    def _set_window_flag(self, flag: Qt.WindowType, enabled: bool) -> None:
        current = bool(self.windowFlags() & flag)
        if current != enabled:
            self.setWindowFlag(flag, enabled)

    def close_permanently(self) -> None:
        self._allow_close = True
        self.close()

    def _schedule_render(self) -> None:
        self._render_scheduled = True
        if self._interaction_active or self._render_timer.isActive():
            return
        self._render_timer.start()

    def _render_entries(self) -> None:
        if self._interaction_active:
            self._render_scheduled = True
            return

        entries = self._model.entries
        current_ids = {entry.entry_id for entry in entries}
        for entry_id in tuple(self._entry_widgets):
            if entry_id in current_ids:
                continue
            widget = self._entry_widgets.pop(entry_id)
            self._items_layout.removeWidget(widget)
            widget.deleteLater()

        added = False
        for index, entry in enumerate(entries):
            widget = self._entry_widgets.get(entry.entry_id)
            if widget is None:
                widget = TranslationItem(
                    entry,
                    self._font_size,
                    self._show_original,
                    self._content,
                )
                self._entry_widgets[entry.entry_id] = widget
                self._items_layout.insertWidget(index, widget)
                added = True
            else:
                widget.apply_style(self._font_size, self._show_original)

        self._render_scheduled = False
        if added and self._auto_scroll:
            QTimer.singleShot(0, self._scroll_to_bottom)

    def _begin_interaction(self) -> None:
        self._interaction_active = True
        self._geometry_commit_timer.stop()
        if self._render_timer.isActive():
            self._render_timer.stop()
            self._render_scheduled = True

    def _finish_interaction(self) -> None:
        self._interaction_active = False
        self._schedule_geometry_commit()
        self._flush_pending_render()

    def _flush_pending_render(self) -> None:
        if self._render_scheduled and not self._render_timer.isActive():
            self._render_timer.start()

    def _scroll_to_bottom(self) -> None:
        bar = self.scroll_area.verticalScrollBar()
        bar.setValue(bar.maximum())

    def _on_scroll_changed(self, value: int) -> None:
        bar = self.scroll_area.verticalScrollBar()
        self._auto_scroll = value >= bar.maximum()

    def _exclude_from_capture(self) -> None:
        if self._capture_excluder is not None and self.isVisible():
            excluded = self._capture_excluder.exclude_from_capture(int(self.winId()))
            if not excluded and not self._capture_warning_emitted:
                self._capture_warning_emitted = True
                self.capture_exclusion_failed.emit()

    def showEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().showEvent(event)
        if not self._has_saved_position:
            screen = QGuiApplication.primaryScreen()
            if screen is not None:
                area = screen.availableGeometry()
                self.move(area.right() - self.width() - 32, area.top() + 48)
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

    def moveEvent(self, event: QMoveEvent) -> None:
        super().moveEvent(event)
        self._schedule_geometry_commit()

    def resizeEvent(self, event: QResizeEvent) -> None:
        super().resizeEvent(event)
        self._schedule_geometry_commit()

    def _schedule_geometry_commit(self) -> None:
        if hasattr(self, "_geometry_commit_timer"):
            self._geometry_commit_timer.start()

    def _emit_geometry(self) -> None:
        # Native system move/resize can consume the Qt mouse-release event. The
        # quiet timer is therefore also the fallback interaction boundary.
        self._interaction_active = False
        self._flush_pending_render()
        self.geometry_changed.emit(self.x(), self.y(), self.width(), self.height())
