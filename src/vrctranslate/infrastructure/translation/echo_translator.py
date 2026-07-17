from __future__ import annotations

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import TranslationCapabilities
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


class EchoTranslator:
    """Offline adapter for verifying UI and OSC without claiming translation."""

    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="test",
            display_name="测试回显（不翻译）",
            online=False,
            supports_auto_detect=True,
            supports_batch=True,
            realtime_recommended=True,
            requires_api_key=False,
        )

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        del profile
        text = normalize_text(request.text)
        return TranslationResult(
            request_id=request.request_id,
            original=text,
            translated=f"[测试回显 {request.target_language}] {text}",
            source_language=request.source_language,
            target_language=request.target_language,
            purpose=request.purpose,
        )

    def translate_batch(
        self,
        requests: list[TranslationRequest],
        profile: TranslationProfile,
    ) -> list[TranslationResult]:
        return [self.translate(request, profile) for request in requests]
