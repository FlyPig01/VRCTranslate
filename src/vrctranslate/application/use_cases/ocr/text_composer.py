from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable

from vrctranslate.domain.ocr import OcrText
from vrctranslate.domain.text_rules import normalize_text


_CJK = re.compile(r"[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]")
_LIST_PREFIX = re.compile(
    r"^\s*(?:[•●▪◦‣⁃·*\-–—]|\(?\d{1,3}[.)、]|[一二三四五六七八九十]+[、.])\s*"
)
_NUMBERED_HEADING = re.compile(r"^\s*\d+(?:\.\d+)+\s+\S")
_SENTENCE_END = set(".!?;。！？；：")
_NO_SPACE_BEFORE = set(",.!?;:%)]}，。！？；：、）】》」』")
_NO_SPACE_AFTER = set("([{（【《「『")
_MAX_BLOCK_LINES = 6


@dataclass(frozen=True, slots=True)
class _Segment:
    text: str
    confidence: float
    left: int
    top: int
    right: int
    bottom: int
    box: tuple[tuple[int, int], ...]
    canvas_size: tuple[int, int]
    background_luminance: float

    @property
    def width(self) -> int:
        return max(1, self.right - self.left)

    @property
    def height(self) -> int:
        return max(1, self.bottom - self.top)

    @property
    def center_x(self) -> float:
        return (self.left + self.right) / 2

    @property
    def center_y(self) -> float:
        return (self.top + self.bottom) / 2


@dataclass(slots=True)
class _Line:
    segments: list[_Segment]

    @property
    def left(self) -> int:
        return min(item.left for item in self.segments)

    @property
    def top(self) -> int:
        return min(item.top for item in self.segments)

    @property
    def right(self) -> int:
        return max(item.right for item in self.segments)

    @property
    def bottom(self) -> int:
        return max(item.bottom for item in self.segments)

    @property
    def width(self) -> int:
        return max(1, self.right - self.left)

    @property
    def height(self) -> int:
        return max(1, self.bottom - self.top)

    @property
    def center_x(self) -> float:
        return (self.left + self.right) / 2

    @property
    def center_y(self) -> float:
        return (self.top + self.bottom) / 2

    @property
    def text(self) -> str:
        ordered = sorted(self.segments, key=lambda item: item.left)
        return _join_fragments(item.text for item in ordered)


def _join_pair(left: str, right: str) -> str:
    if not left:
        return right
    if not right:
        return left
    if left[-1].isspace() or right[0].isspace():
        return left + right
    if (
        right[0] in _NO_SPACE_BEFORE
        or left[-1] in _NO_SPACE_AFTER
        or _CJK.fullmatch(left[-1])
        or _CJK.fullmatch(right[0])
    ):
        return left + right
    return f"{left} {right}"


def _join_fragments(fragments: Iterable[str]) -> str:
    output = ""
    for fragment in fragments:
        output = _join_pair(output, normalize_text(fragment))
    return output


def _as_segment(item: OcrText) -> _Segment | None:
    if len(item.box) < 2:
        return None
    xs = [point[0] for point in item.box]
    ys = [point[1] for point in item.box]
    left, right = min(xs), max(xs)
    top, bottom = min(ys), max(ys)
    if right <= left or bottom <= top:
        return None
    return _Segment(
        normalize_text(item.text),
        item.confidence,
        left,
        top,
        right,
        bottom,
        item.box,
        item.canvas_size,
        item.background_luminance,
    )


def _vertical_match(line: _Line, segment: _Segment) -> bool:
    overlap = min(line.bottom, segment.bottom) - max(line.top, segment.top)
    if overlap > 0 and overlap / min(line.height, segment.height) >= 0.35:
        return True
    return abs(line.center_y - segment.center_y) <= max(
        line.height, segment.height
    ) * 0.55


def _build_rows(segments: list[_Segment]) -> list[_Line]:
    rows: list[_Line] = []
    for segment in sorted(segments, key=lambda item: (item.center_y, item.left)):
        matches = [row for row in rows if _vertical_match(row, segment)]
        if not matches:
            rows.append(_Line([segment]))
            continue
        closest = min(matches, key=lambda row: abs(row.center_y - segment.center_y))
        closest.segments.append(segment)
    return rows


def _split_distant_row(row: _Line) -> list[_Line]:
    result: list[_Line] = []
    current: list[_Segment] = []
    for segment in sorted(row.segments, key=lambda item: item.left):
        if current:
            previous = current[-1]
            gap = segment.left - previous.right
            if gap > max(previous.height, segment.height) * 2.2:
                result.append(_Line(current))
                current = []
        current.append(segment)
    if current:
        result.append(_Line(current))
    return result


def _can_follow(block: list[_Line], current: _Line) -> bool:
    previous = block[-1]
    vertical_gap = current.top - previous.bottom
    minimum_height = min(previous.height, current.height)
    if vertical_gap < -minimum_height * 0.35:
        return False
    if vertical_gap > max(previous.height, current.height) * 1.05:
        return False
    if len(block) >= _MAX_BLOCK_LINES:
        return False
    if _LIST_PREFIX.match(current.text):
        return False
    if _NUMBERED_HEADING.match(current.text) or _NUMBERED_HEADING.match(previous.text):
        return False
    height_ratio = max(previous.height, current.height) / minimum_height
    if height_ratio > 1.45:
        return False
    if (
        len(previous.text) <= 28
        and len(current.text) >= max(36, round(len(previous.text) * 1.8))
        and previous.height >= current.height * 1.05
    ):
        return False
    if (
        previous.text
        and previous.text[-1] in _SENTENCE_END
        and vertical_gap > minimum_height * 0.55
    ):
        return False
    overlap = min(previous.right, current.right) - max(previous.left, current.left)
    overlap_ratio = max(0, overlap) / min(previous.width, current.width)
    center_distance = abs(previous.center_x - current.center_x)
    left_distance = abs(previous.left - current.left)
    left_aligned = left_distance <= max(10, round(minimum_height * 1.1))
    continuation_indent = (
        _LIST_PREFIX.match(previous.text) is not None
        and current.left >= previous.left
        and current.left - previous.left <= max(previous.height * 3, previous.width * 0.3)
    )
    return (
        left_aligned
        or continuation_indent
        or overlap_ratio >= 0.45
        or center_distance <= max(previous.width, current.width) * 0.2
    )


def _group_lines(lines: list[_Line]) -> list[list[_Line]]:
    blocks: list[list[_Line]] = []
    for line in sorted(lines, key=lambda item: (item.top, item.left)):
        matches = [block for block in blocks if _can_follow(block, line)]
        if not matches:
            blocks.append([line])
            continue
        closest = min(
            matches,
            key=lambda block: (
                max(0, line.top - block[-1].bottom),
                abs(line.center_x - block[-1].center_x),
            ),
        )
        closest.append(line)
    return blocks


def _to_ocr_text(lines: list[_Line]) -> OcrText:
    ordered = sorted(lines, key=lambda item: (item.top, item.left))
    text = _join_fragments(line.text for line in ordered)
    segments = [segment for line in ordered for segment in line.segments]
    weight = sum(max(1, len(segment.text)) for segment in segments)
    confidence = sum(
        segment.confidence * max(1, len(segment.text)) for segment in segments
    ) / weight
    left = min(line.left for line in ordered)
    top = min(line.top for line in ordered)
    right = max(line.right for line in ordered)
    bottom = max(line.bottom for line in ordered)
    box = ((left, top), (right, top), (right, bottom), (left, bottom))
    line_boxes = tuple(segment.box for segment in segments if segment.box)
    canvas_size = next(
        (segment.canvas_size for segment in segments if segment.canvas_size != (0, 0)),
        (0, 0),
    )
    background_luminance = sum(
        segment.background_luminance * max(1, len(segment.text))
        for segment in segments
    ) / weight
    return OcrText(
        text,
        confidence,
        box,
        line_boxes,
        canvas_size,
        background_luminance,
    )


def compose_ocr_texts(items: list[OcrText]) -> list[OcrText]:
    """Restore reading order and join nearby OCR lines without mixing distant text."""

    positioned: list[_Segment] = []
    unpositioned: list[OcrText] = []
    for item in items:
        segment = _as_segment(item)
        if segment is None:
            text = normalize_text(item.text)
            if text:
                unpositioned.append(
                    OcrText(
                        text,
                        item.confidence,
                        item.box,
                        item.line_boxes,
                        item.canvas_size,
                        item.background_luminance,
                    )
                )
        elif segment.text:
            positioned.append(segment)
    rows = _build_rows(positioned)
    lines = [line for row in rows for line in _split_distant_row(row)]
    composed = [_to_ocr_text(block) for block in _group_lines(lines)]
    ordered = sorted(composed, key=lambda item: (item.box[0][1], item.box[0][0]))
    return ordered + unpositioned
