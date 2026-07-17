from __future__ import annotations

from dataclasses import dataclass
from statistics import median
from time import monotonic

from PySide6.QtCore import QPointF, QRectF, Qt, QTimer, Signal
from PySide6.QtGui import (
    QColor,
    QFont,
    QPaintEvent,
    QPainter,
    QTextLayout,
    QTextOption,
)
from PySide6.QtWidgets import QWidget

from vrctranslate.application.dto import UiSettings
from vrctranslate.application.ports.window_capture import WindowCaptureExcluder
from vrctranslate.domain.ocr import CaptureRegion, OcrText, WindowInfo
from vrctranslate.presentation.qt.font_utils import font_with_pixel_height
from vrctranslate.presentation.qt.windows.ocr_geometry import logical_rect_for_region


@dataclass(frozen=True, slots=True)
class _InlineEntry:
    request_id: str
    translated: str
    source: OcrText
    expires_at: float | None


@dataclass(frozen=True, slots=True)
class _PreparedInlineEntry:
    entry: _InlineEntry
    line_rects: tuple[QRectF, ...]
    source_rect: QRectF


class OcrInlineWindow(QWidget):
    """One click-through paint surface aligned exactly with the OCR region."""

    capture_exclusion_failed = Signal()

    def __init__(
        self,
        capture_excluder: WindowCaptureExcluder | None = None,
    ) -> None:
        super().__init__(None)
        self._capture_excluder = capture_excluder
        self._target: WindowInfo | None = None
        self._region: CaptureRegion | None = None
        self._entries: dict[str, _InlineEntry] = {}
        self._display_mode = "overlay"
        self._opacity = 0.9
        self._auto_contrast = True
        self._target_visible = True
        self._allow_close = False
        self._exclusion_reported = False
        self.setObjectName("ocrInlineWindow")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.WindowTransparentForInput
            | Qt.WindowType.WindowDoesNotAcceptFocus
        )
        self._expiry_timer = QTimer(self)
        self._expiry_timer.setInterval(250)
        self._expiry_timer.timeout.connect(self._remove_expired)
        self._expiry_timer.start()

    def apply_settings(self, settings: UiSettings) -> None:
        self._display_mode = settings.ocr_display_mode
        self._opacity = settings.ocr_inline_opacity
        self._auto_contrast = settings.ocr_inline_auto_contrast
        self._update_visibility()
        self.update()

    def set_target(self, target: WindowInfo, region: CaptureRegion) -> None:
        self._target = target
        self._region = region
        self.setGeometry(logical_rect_for_region(target, region))
        self._update_visibility()

    def set_target_visible(self, visible: bool) -> None:
        self._target_visible = visible
        self._update_visibility()

    def add_translation(
        self,
        request_id: str,
        source: OcrText,
        translated: str,
        display_seconds: float | None,
    ) -> None:
        if not translated.strip() or not source.box:
            return
        self._remove_overlapping(source)
        self._entries[request_id] = _InlineEntry(
            request_id,
            translated.strip(),
            source,
            None
            if display_seconds is None
            else monotonic() + max(2.0, display_seconds),
        )
        self._update_visibility()
        self.update()

    def clear(self) -> None:
        self._entries.clear()
        self.hide()
        self.update()

    def close_permanently(self) -> None:
        self._allow_close = True
        self._expiry_timer.stop()
        self.close()

    def paintEvent(self, event: QPaintEvent) -> None:
        del event
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.TextAntialiasing)
        prepared = self._prepare_entries()
        for current in prepared:
            self._paint_entry(
                painter,
                current,
                self._layout_bounds(current, prepared),
            )

    def showEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().showEvent(event)
        if self._capture_excluder is None:
            return
        if not self._capture_excluder.exclude_from_capture(int(self.winId())):
            if not self._exclusion_reported:
                self._exclusion_reported = True
                self.capture_exclusion_failed.emit()

    def closeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        if self._allow_close:
            event.accept()
        else:
            self.hide()
            event.ignore()

    def _paint_entry(
        self,
        painter: QPainter,
        prepared: _PreparedInlineEntry,
        layout_bounds: QRectF,
    ) -> None:
        entry = prepared.entry
        rects = prepared.line_rects
        union = prepared.source_rect

        light_background = self._auto_contrast and entry.source.background_luminance >= 0.62
        background = QColor(244, 247, 250) if light_background else QColor(8, 18, 30)
        foreground = QColor(12, 25, 40) if light_background else QColor(255, 255, 255)
        background.setAlpha(round(255 * self._opacity))
        for rect in rects:
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(background)
            painter.drawRoundedRect(rect.adjusted(-2, -1, 2, 1), 3, 3)

        text_rect = QRectF(
            union.left(),
            union.top(),
            max(1.0, layout_bounds.width()),
            max(1.0, layout_bounds.height()),
        )

        source_heights = [rect.height() for rect in rects]
        initial_size = max(9, round(median(source_heights) * 0.88))
        layout = self._fit_layout(entry.translated, text_rect, initial_size)
        text_origin = text_rect.topLeft()
        painter.save()
        painter.setClipRect(layout_bounds.adjusted(-2, -1, 2, 1))
        for line_rect in self._layout_line_rects(layout, text_origin, text_rect.width()):
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(background)
            painter.drawRoundedRect(line_rect.adjusted(-2, -1, 2, 1), 3, 3)

        shadow = QColor(255, 255, 255, 100) if light_background else QColor(0, 0, 0, 150)
        painter.setPen(shadow)
        layout.draw(painter, text_origin + QPointF(1, 1))
        painter.setPen(foreground)
        layout.draw(painter, text_origin)
        painter.restore()

    def _prepare_entries(self) -> tuple[_PreparedInlineEntry, ...]:
        prepared: list[_PreparedInlineEntry] = []
        for entry in self._entries.values():
            canvas_width, canvas_height = entry.source.canvas_size
            if canvas_width <= 0 or canvas_height <= 0:
                continue
            scale_x = self.width() / canvas_width
            scale_y = self.height() / canvas_height
            source_boxes = entry.source.line_boxes or (entry.source.box,)
            rects = tuple(
                rect
                for rect in (
                    self._logical_box(box, scale_x, scale_y)
                    for box in source_boxes
                )
                if rect.isValid()
            )
            if not rects:
                continue
            source_rect = QRectF(rects[0])
            for rect in rects[1:]:
                source_rect = source_rect.united(rect)
            prepared.append(_PreparedInlineEntry(entry, rects, source_rect))
        return tuple(prepared)

    def _layout_bounds(
        self,
        current: _PreparedInlineEntry,
        entries: tuple[_PreparedInlineEntry, ...],
    ) -> QRectF:
        source = current.source_rect
        right = float(self.width())
        bottom = float(self.height())
        for other in entries:
            if other is current:
                continue
            candidate = other.source_rect
            vertical_overlap = min(source.bottom(), candidate.bottom()) - max(
                source.top(), candidate.top()
            )
            if candidate.left() > source.left() and vertical_overlap > 0:
                overlap_ratio = vertical_overlap / max(
                    1.0, min(source.height(), candidate.height())
                )
                if overlap_ratio >= 0.25:
                    right = min(right, candidate.left() - 6)

            horizontal_overlap = min(source.right(), candidate.right()) - max(
                source.left(), candidate.left()
            )
            overlap_ratio = max(0.0, horizontal_overlap) / max(
                1.0, min(source.width(), candidate.width())
            )
            same_column = overlap_ratio >= 0.2 or abs(
                source.left() - candidate.left()
            ) <= max(16.0, source.height() * 1.5)
            if candidate.top() > source.top() and same_column:
                bottom = min(bottom, candidate.top() - 4)

        right = max(source.right(), right)
        bottom = max(source.bottom(), bottom)
        return QRectF(
            source.left(),
            source.top(),
            max(1.0, right - source.left()),
            max(1.0, bottom - source.top()),
        )

    @staticmethod
    def _logical_box(
        box: tuple[tuple[int, int], ...], scale_x: float, scale_y: float
    ) -> QRectF:
        if not box:
            return QRectF()
        xs = [point[0] * scale_x for point in box]
        ys = [point[1] * scale_y for point in box]
        return QRectF(min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys))

    def _fit_layout(
        self,
        text: str,
        rect: QRectF,
        initial_size: int,
    ) -> QTextLayout:
        minimum = max(6, round(initial_size * 0.45))
        chosen: QTextLayout | None = None
        for size in range(initial_size, minimum - 1, -1):
            font = font_with_pixel_height(self, self.font(), size)
            layout, height = self._create_layout(text, font, rect.width())
            chosen = layout
            if height <= rect.height():
                break
        assert chosen is not None
        return chosen

    @staticmethod
    def _create_layout(
        text: str,
        font: QFont,
        width: float,
    ) -> tuple[QTextLayout, float]:
        layout = QTextLayout(text, font)
        option = QTextOption()
        option.setWrapMode(QTextOption.WrapMode.WrapAtWordBoundaryOrAnywhere)
        layout.setTextOption(option)
        layout.beginLayout()
        y = 0.0
        while True:
            line = layout.createLine()
            if not line.isValid():
                break
            line.setLineWidth(max(1.0, width))
            line.setPosition(QPointF(0, y))
            y += line.height()
        layout.endLayout()
        return layout, y

    @staticmethod
    def _layout_line_rects(
        layout: QTextLayout,
        origin: QPointF,
        maximum_width: float,
    ) -> list[QRectF]:
        rects: list[QRectF] = []
        for index in range(layout.lineCount()):
            line = layout.lineAt(index)
            width = min(maximum_width, max(1.0, line.naturalTextWidth()))
            rects.append(
                QRectF(
                    origin.x() + line.position().x(),
                    origin.y() + line.position().y(),
                    width,
                    line.height(),
                )
            )
        return rects

    def _remove_overlapping(self, source: OcrText) -> None:
        current = self._normalized_rect(source)
        if not current.isValid():
            return
        for request_id, entry in tuple(self._entries.items()):
            existing = self._normalized_rect(entry.source)
            intersection = current.intersected(existing)
            smaller = min(current.width() * current.height(), existing.width() * existing.height())
            if smaller > 0 and intersection.width() * intersection.height() / smaller >= 0.35:
                self._entries.pop(request_id, None)

    @staticmethod
    def _normalized_rect(source: OcrText) -> QRectF:
        width, height = source.canvas_size
        if not source.box or width <= 0 or height <= 0:
            return QRectF()
        xs = [point[0] / width for point in source.box]
        ys = [point[1] / height for point in source.box]
        return QRectF(min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys))

    def _remove_expired(self) -> None:
        now = monotonic()
        changed = False
        for request_id, entry in tuple(self._entries.items()):
            if entry.expires_at is not None and entry.expires_at <= now:
                self._entries.pop(request_id, None)
                changed = True
        if changed:
            self._update_visibility()
            self.update()

    def _update_visibility(self) -> None:
        should_show = (
            self._display_mode in {"inline", "both"}
            and self._target is not None
            and self._region is not None
            and self._target_visible
            and bool(self._entries)
        )
        if should_show:
            self.show()
            self.raise_()
        else:
            self.hide()
