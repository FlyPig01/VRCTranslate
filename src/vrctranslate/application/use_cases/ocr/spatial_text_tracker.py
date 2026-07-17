from __future__ import annotations

from dataclasses import dataclass

from vrctranslate.domain.ocr import OcrText
from vrctranslate.domain.text_rules import normalize_text


@dataclass(frozen=True, slots=True)
class _Bounds:
    left: int
    top: int
    right: int
    bottom: int

    @property
    def area(self) -> int:
        return max(1, self.right - self.left) * max(1, self.bottom - self.top)


def _bounds(item: OcrText) -> _Bounds | None:
    if len(item.box) < 2:
        return None
    xs = [point[0] for point in item.box]
    ys = [point[1] for point in item.box]
    left, right = min(xs), max(xs)
    top, bottom = min(ys), max(ys)
    if right <= left or bottom <= top:
        return None
    return _Bounds(left, top, right, bottom)


def _overlap_ratio(left: _Bounds, right: _Bounds) -> float:
    width = max(0, min(left.right, right.right) - max(left.left, right.left))
    height = max(0, min(left.bottom, right.bottom) - max(left.top, right.top))
    intersection = width * height
    return intersection / min(left.area, right.area)


class SpatialTextTracker:
    """Report text that is new or changed at a spatial OCR location.

    The snapshot is replaced on every OCR pass. This lets a changing subtitle be
    translated while stable labels elsewhere in the same capture region stay quiet.
    """

    def __init__(self, overlap_threshold: float = 0.45) -> None:
        self._overlap_threshold = overlap_threshold
        self._previous: list[OcrText] = []

    def changed(self, current: list[OcrText]) -> list[OcrText]:
        changed: list[OcrText] = []
        for item in current:
            text = normalize_text(item.text).casefold()
            if not text or not self._same_text_at_same_place(item, text):
                changed.append(item)
        self._previous = list(current)
        return changed

    def _same_text_at_same_place(self, current: OcrText, text: str) -> bool:
        current_bounds = _bounds(current)
        for previous in self._previous:
            if normalize_text(previous.text).casefold() != text:
                continue
            previous_bounds = _bounds(previous)
            if current_bounds is None or previous_bounds is None:
                return True
            if _overlap_ratio(current_bounds, previous_bounds) >= self._overlap_threshold:
                return True
        return False

    def clear(self) -> None:
        self._previous.clear()
