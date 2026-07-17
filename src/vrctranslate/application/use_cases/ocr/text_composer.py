from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable

from vrctranslate.domain.ocr import OcrText
from vrctranslate.domain.text_rules import normalize_text


_CJK = re.compile(r"[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]")
_NO_SPACE_BEFORE = set(",.!?;:%)]}，。！？；：、）】》」』")
_NO_SPACE_AFTER = set("([{（【《「『")


@dataclass(frozen=True, slots=True)
class _Segment:
    text: str
    confidence: float
    left: int
    top: int
    right: int
    bottom: int

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


def _can_follow(previous: _Line, current: _Line) -> bool:
    vertical_gap = current.top - previous.bottom
    minimum_height = min(previous.height, current.height)
    if vertical_gap < -minimum_height * 0.35:
        return False
    if vertical_gap > max(previous.height, current.height) * 1.4:
        return False
    overlap = min(previous.right, current.right) - max(previous.left, current.left)
    overlap_ratio = max(0, overlap) / min(previous.width, current.width)
    center_distance = abs(previous.center_x - current.center_x)
    return overlap_ratio >= 0.15 or center_distance <= max(
        previous.width, current.width
    ) * 0.35


def _group_lines(lines: list[_Line]) -> list[list[_Line]]:
    blocks: list[list[_Line]] = []
    for line in sorted(lines, key=lambda item: (item.top, item.left)):
        matches = [block for block in blocks if _can_follow(block[-1], line)]
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
    return OcrText(text, confidence, box)


def compose_ocr_texts(items: list[OcrText]) -> list[OcrText]:
    """Restore reading order and join nearby OCR lines without mixing distant text."""

    positioned: list[_Segment] = []
    unpositioned: list[OcrText] = []
    for item in items:
        segment = _as_segment(item)
        if segment is None:
            text = normalize_text(item.text)
            if text:
                unpositioned.append(OcrText(text, item.confidence, item.box))
        elif segment.text:
            positioned.append(segment)
    rows = _build_rows(positioned)
    lines = [line for row in rows for line in _split_distant_row(row)]
    composed = [_to_ocr_text(block) for block in _group_lines(lines)]
    ordered = sorted(composed, key=lambda item: (item.box[0][1], item.box[0][0]))
    return ordered + unpositioned
