from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


@dataclass(frozen=True, slots=True)
class TranslationCapabilities:
    provider: str
    display_name: str
    online: bool
    supports_auto_detect: bool
    supports_batch: bool
    realtime_recommended: bool
    requires_api_key: bool
    supported_languages: tuple[str, ...] = ()
    available: bool = True


class TranslationAdapter(Protocol):
    def capabilities(self) -> TranslationCapabilities: ...

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult: ...


class Translator(Protocol):
    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult: ...

    def translate_batch(
        self,
        requests: list[TranslationRequest],
        profile: TranslationProfile,
    ) -> list[TranslationResult]: ...
