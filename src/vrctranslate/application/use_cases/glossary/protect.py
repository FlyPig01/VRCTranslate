from __future__ import annotations

import re
import secrets
from dataclasses import dataclass

from vrctranslate.domain.glossary import GlossaryInstruction, GlossaryMatch


_RESIDUAL_TOKEN = re.compile(r"VRCG[A-Z0-9]{6,}", re.IGNORECASE)


@dataclass(frozen=True, slots=True)
class _Binding:
    token: str
    source: str
    target: str


@dataclass(frozen=True, slots=True)
class GlossaryProtection:
    text: str
    bindings: tuple[_Binding, ...] = ()

    @property
    def instructions(self) -> tuple[GlossaryInstruction, ...]:
        seen: set[tuple[str, str]] = set()
        output: list[GlossaryInstruction] = []
        for binding in self.bindings:
            key = (binding.source, binding.target)
            if key not in seen:
                output.append(GlossaryInstruction(*key))
                seen.add(key)
        return tuple(output)

    def restore_sources(self) -> str:
        output = self.text
        for binding in self.bindings:
            output = output.replace(binding.token, binding.source)
        return output

    def restore_targets(self, translated: str) -> str | None:
        output = translated
        for binding in self.bindings:
            pattern = re.compile(re.escape(binding.token), re.IGNORECASE)
            if len(pattern.findall(output)) != 1:
                return None
            output = pattern.sub(binding.target, output)
        return None if _RESIDUAL_TOKEN.search(output) else output


def _prefix(text: str) -> str:
    while True:
        candidate = f"VRCG{secrets.token_hex(3).upper()}"
        if candidate.casefold() not in text.casefold():
            return candidate


def protect_matches(
    text: str,
    matches: tuple[GlossaryMatch, ...],
    previous: GlossaryProtection | None = None,
) -> GlossaryProtection:
    if not matches:
        return previous or GlossaryProtection(text)
    existing = previous.bindings if previous is not None else ()
    prefix = _prefix(text)
    parts: list[str] = []
    bindings = list(existing)
    cursor = 0
    for offset, match in enumerate(matches, start=len(bindings) + 1):
        token = f"{prefix}{offset:04d}"
        parts.append(text[cursor:match.start])
        parts.append(token)
        bindings.append(_Binding(token, match.source_text, match.entry.target))
        cursor = match.end
    parts.append(text[cursor:])
    return GlossaryProtection("".join(parts), tuple(bindings))
