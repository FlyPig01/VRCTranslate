from __future__ import annotations

import re
import secrets
from dataclasses import dataclass


_PROTECTED_VALUE = re.compile(
    r"""
    `[^`\r\n]+`
    |https?://[^\s<>"']+
    |(?<![\w.+-])[A-Z]:\\[^\s\r\n<>"|?*]+
    |\\\\[^\s\\/:*?"<>|]+\\[^\s\r\n<>"|?*]+
    |(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?![\w.-])
    |(?<!\w)@[\w.-]+(?!\w)
    |(?<!\w)/(?:[A-Za-z0-9_.~-]+/)*[A-Za-z0-9_.~-]+(?![\w/])
    |(?<!\w)(?:VRChat|OSC|[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_.-]+)(?!\w)
    |(?<!\w)[vV]?\d+(?:[.:/-]\d+)+(?!\w)
    |(?<!\w)[+-]?\d+(?:[.,]\d+)*(?:%|‰)?(?!\w)
    """,
    re.IGNORECASE | re.VERBOSE,
)
_RESIDUAL_TOKEN = re.compile(r"VRCKP[A-F0-9]{10,}", re.IGNORECASE)


@dataclass(frozen=True, slots=True)
class _InvariantBinding:
    token: str
    value: str


@dataclass(frozen=True, slots=True)
class InvariantProtection:
    """Protect values that must survive every translation provider unchanged."""

    text: str
    bindings: tuple[_InvariantBinding, ...] = ()

    def restore(self, translated: str) -> str | None:
        output = translated
        for binding in self.bindings:
            pattern = _tolerant_token_pattern(binding.token)
            if len(pattern.findall(output)) != 1:
                return None
            output = pattern.sub(lambda _match, value=binding.value: value, output)
        return None if _RESIDUAL_TOKEN.search(output) else output


def protect_invariants(text: str) -> InvariantProtection:
    matches = tuple(_PROTECTED_VALUE.finditer(text))
    if not matches:
        return InvariantProtection(text)
    prefix = _unique_prefix(text)
    parts: list[str] = []
    bindings: list[_InvariantBinding] = []
    cursor = 0
    for index, match in enumerate(matches, start=1):
        value = match.group(0)
        token = f"{prefix}{index:04X}"
        parts.append(text[cursor : match.start()])
        parts.append(token)
        bindings.append(_InvariantBinding(token, value))
        cursor = match.end()
    parts.append(text[cursor:])
    return InvariantProtection("".join(parts), tuple(bindings))


def _unique_prefix(text: str) -> str:
    while True:
        candidate = f"VRCKP{secrets.token_hex(3).upper()}"
        if candidate.casefold() not in text.casefold():
            return candidate


def _tolerant_token_pattern(token: str) -> re.Pattern[str]:
    # Some machine translators insert spaces into unknown identifiers.  Accept
    # those harmless changes while still requiring every token exactly once.
    separated = r"[\s\u200b]*".join(re.escape(character) for character in token)
    return re.compile(separated, re.IGNORECASE)
