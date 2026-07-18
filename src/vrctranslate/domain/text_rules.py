from __future__ import annotations

import re
from collections import deque
from difflib import SequenceMatcher
from time import monotonic

from vrctranslate.domain.chatbox import MessageFormat


def normalize_text(text: str) -> str:
    lines = (line.strip() for line in text.replace("\r\n", "\n").split("\n"))
    return "\n".join(line for line in lines if line).strip()


def format_chatbox_message(
    original: str,
    translated: str,
    message_format: MessageFormat,
) -> str:
    original = normalize_text(original)
    translated = normalize_text(translated)
    if message_format == MessageFormat.ORIGINAL_THEN_TRANSLATION:
        return f"{original}\n\n{translated}" if original else translated
    if message_format == MessageFormat.TRANSLATION_THEN_ORIGINAL:
        return f"{translated}\n\n{original}" if original else translated
    return translated


def utf16_units(text: str) -> int:
    return len(text.encode("utf-16-le")) // 2


def truncate_utf16(text: str, maximum_units: int) -> str:
    if maximum_units <= 0:
        return ""
    output: list[str] = []
    used = 0
    for character in text:
        units = utf16_units(character)
        if used + units > maximum_units:
            break
        output.append(character)
        used += units
    return "".join(output)


def split_utf16(text: str, maximum_units: int) -> list[str]:
    if not text or maximum_units <= 0:
        return []
    chunks: list[str] = []
    remaining = text
    while remaining:
        chunk = truncate_utf16(remaining, maximum_units)
        if not chunk:
            break
        if len(chunk) < len(remaining):
            split_at = max(chunk.rfind(" "), chunk.rfind("\n"))
            if split_at > max(0, len(chunk) // 2):
                chunk = chunk[:split_at].rstrip()
        chunks.append(chunk)
        remaining = remaining[len(chunk) :].lstrip()
    return chunks


class TextDeduplicator:
    def __init__(self, window_seconds: float = 8.0, similarity: float = 0.9) -> None:
        self.window_seconds = window_seconds
        self.similarity = similarity
        self._items: deque[tuple[float, str]] = deque()

    def accept(self, text: str, now: float | None = None) -> bool:
        timestamp = monotonic() if now is None else now
        normalized = re.sub(r"\W+", "", text.casefold(), flags=re.UNICODE)
        if not normalized:
            return False
        while self._items and timestamp - self._items[0][0] > self.window_seconds:
            self._items.popleft()
        for _seen_at, previous in self._items:
            if normalized == previous:
                return False
            if SequenceMatcher(None, normalized, previous).ratio() >= self.similarity:
                return False
        self._items.append((timestamp, normalized))
        return True

    def clear(self) -> None:
        self._items.clear()


def frame_signature_changed(
    previous: bytes | None,
    current: bytes,
    threshold: float,
) -> bool:
    if previous is None or len(previous) != len(current):
        return True
    if not current:
        return False
    mean_difference = sum(abs(left - right) for left, right in zip(previous, current)) / len(current)
    return mean_difference >= threshold
