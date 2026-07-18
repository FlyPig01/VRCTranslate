from __future__ import annotations

import re

import wanakana


class WanaKanaRomajiConverter:
    """Small WanaKana adapter with common Hepburn input compatibility."""

    def to_hiragana(self, text: str) -> str:
        normalized = text.lower()
        # WanaKana expects ``maccha`` while users commonly type ``matcha``.
        normalized = re.sub(r"tch", "cch", normalized)
        # Hepburn writes ん as m before b/m/p in words such as shimbun.
        normalized = re.sub(r"m(?=[bmp])", "n", normalized)
        return str(wanakana.to_kana(normalized))
