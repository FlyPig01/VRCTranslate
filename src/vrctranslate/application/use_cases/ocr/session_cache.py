from __future__ import annotations

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


CacheKey = tuple[str, str, str, str, tuple[str, ...]]


class SessionTranslationCache:
    """Short-lived in-memory cache; it is cleared whenever OCR stops."""

    def __init__(self) -> None:
        self._values: dict[CacheKey, TranslationResult] = {}

    @staticmethod
    def key(request: TranslationRequest, profile: TranslationProfile) -> CacheKey:
        context = (
            tuple(normalize_text(item).casefold() for item in request.context)
            if profile.provider == "openai_compatible"
            else ()
        )
        return (
            normalize_text(request.text).casefold(),
            request.source_language,
            request.target_language,
            profile.id,
            context,
        )

    def get(self, key: CacheKey) -> TranslationResult | None:
        return self._values.get(key)

    def put(self, key: CacheKey, result: TranslationResult) -> None:
        self._values[key] = result

    def clear(self) -> None:
        self._values.clear()
