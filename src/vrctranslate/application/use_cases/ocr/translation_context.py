from __future__ import annotations

from collections import deque
from time import monotonic

from vrctranslate.domain.text_rules import normalize_text


class RecentOcrContext:
    """Keep a small, session-only source-text context for LLM disambiguation."""

    def __init__(
        self,
        max_items: int = 3,
        ttl_seconds: float = 15.0,
        max_characters: int = 500,
    ) -> None:
        self._max_items = max(1, max_items)
        self._ttl_seconds = max(0.1, ttl_seconds)
        self._max_characters = max(1, max_characters)
        self._items: deque[tuple[float, str]] = deque()

    def prepare(self, current_text: str, now: float | None = None) -> tuple[str, ...]:
        timestamp = monotonic() if now is None else now
        self._prune(timestamp)
        context = tuple(text for _, text in self._items)
        text = normalize_text(current_text)
        if text and (not self._items or self._items[-1][1] != text):
            self._items.append((timestamp, text[: self._max_characters]))
            self._trim_to_limits()
        return context

    def clear(self) -> None:
        self._items.clear()

    def _prune(self, now: float) -> None:
        while self._items and now - self._items[0][0] > self._ttl_seconds:
            self._items.popleft()

    def _trim_to_limits(self) -> None:
        while len(self._items) > self._max_items:
            self._items.popleft()
        while (
            len(self._items) > 1
            and sum(len(text) for _, text in self._items) > self._max_characters
        ):
            self._items.popleft()
