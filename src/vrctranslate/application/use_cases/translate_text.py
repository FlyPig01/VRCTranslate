from __future__ import annotations

from vrctranslate.application.dto import TranslationProfile, TranslationSettings
from vrctranslate.application.ports.translator import Translator
from vrctranslate.application.text_preprocessing.japanese_romaji import preprocess_romaji
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


class TranslateText:
    def __init__(self, translator: Translator) -> None:
        self._translator = translator

    def _romaji_enabled(self, request: TranslationRequest, settings: TranslationSettings | TranslationProfile) -> bool:
        if isinstance(settings, TranslationSettings):
            route = settings.ocr_route if request.purpose == "ocr" else settings.self_route
            return route.romaji_to_kana
        return True

    def _preprocess(self, request: TranslationRequest, settings: TranslationSettings | TranslationProfile) -> TranslationRequest:
        enabled = self._romaji_enabled(request, settings)
        converted_text, changed = preprocess_romaji(request.text, request.source_language, enabled)
        if not changed:
            return request
        return TranslationRequest(
            request_id=request.request_id,
            text=converted_text,
            source_language="ja" if request.source_language in ("ja", "auto") else request.source_language,
            target_language=request.target_language,
            purpose=request.purpose,
            context=request.context,
        )

    def execute(
        self,
        request: TranslationRequest,
        settings: TranslationSettings | TranslationProfile,
    ) -> TranslationResult:
        profile = (
            settings.profile_for_purpose(request.purpose)
            if isinstance(settings, TranslationSettings)
            else settings
        )
        request = self._preprocess(request, settings)
        return self._translator.translate(request, profile)

    def execute_batch(
        self,
        requests: list[TranslationRequest],
        settings: TranslationSettings | TranslationProfile,
    ) -> list[TranslationResult]:
        if not requests:
            return []
        profile = (
            settings.profile_for_purpose(requests[0].purpose)
            if isinstance(settings, TranslationSettings)
            else settings
        )
        processed = [self._preprocess(r, settings) for r in requests]
        method = getattr(self._translator, "translate_batch", None)
        if callable(method):
            return list(method(processed, profile))
        return [self._translator.translate(request, profile) for request in processed]
