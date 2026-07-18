from __future__ import annotations

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


CacheKey = tuple[str, str, str, str, int, tuple[str, ...]]


class SessionTranslationCache:
    """Short-lived in-memory cache; it is cleared whenever OCR stops."""

    def __init__(self) -> None:
        self._values: dict[CacheKey, TranslationResult] = {}

    @staticmethod
    def key(
        request: TranslationRequest,
        profile: TranslationProfile,
        glossary_revision: int = 0,
    ) -> CacheKey:
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
            glossary_revision,
            context,
        )

    def get(self, key: CacheKey) -> TranslationResult | None:
        return self._values.get(key)

    def put(self, key: CacheKey, result: TranslationResult) -> None:
        self._values[key] = result

    def clear(self) -> None:
        self._values.clear()
