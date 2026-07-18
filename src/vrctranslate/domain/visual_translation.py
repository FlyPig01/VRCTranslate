from __future__ import annotations

from dataclasses import dataclass

from vrctranslate.domain.glossary import GlossaryInstruction


@dataclass(frozen=True, slots=True)
class VisualTextRegion:
    region_id: str
    bbox: tuple[int, int, int, int]


@dataclass(frozen=True, slots=True)
class EncodedVisualFrame:
    image_bytes: bytes
    mime_type: str
    regions: tuple[VisualTextRegion, ...]


@dataclass(frozen=True, slots=True)
class VisualTranslationRequest:
    request_id: str
    image_bytes: bytes
    mime_type: str
    source_language: str
    target_language: str
    regions: tuple[VisualTextRegion, ...] = ()
    glossary: tuple[GlossaryInstruction, ...] = ()


@dataclass(frozen=True, slots=True)
class VisualRegionTranslation:
    region_id: str
    original: str
    translated: str


@dataclass(frozen=True, slots=True)
class VisualTranslationResult:
    request_id: str
    original: str = ""
    translated: str = ""
    regions: tuple[VisualRegionTranslation, ...] = ()
