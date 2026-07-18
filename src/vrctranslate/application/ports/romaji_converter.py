from __future__ import annotations

from typing import Protocol


class RomajiConverter(Protocol):
    """Convert a continuous romaji span without deciding whether it is Japanese."""

    def to_hiragana(self, text: str) -> str: ...
