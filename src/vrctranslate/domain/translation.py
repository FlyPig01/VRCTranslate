from __future__ import annotations

from dataclasses import dataclass

from vrctranslate.domain.glossary import GlossaryInstruction


@dataclass(frozen=True, slots=True)
class TranslationRequest:
    request_id: str
    text: str
    source_language: str
    target_language: str
    purpose: str = "self"
    context: tuple[str, ...] = ()
    glossary: tuple[GlossaryInstruction, ...] = ()


@dataclass(frozen=True, slots=True)
class TranslationResult:
    request_id: str
    original: str
    translated: str
    source_language: str
    target_language: str
    purpose: str
