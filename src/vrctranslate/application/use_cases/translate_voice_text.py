from __future__ import annotations

from uuid import uuid4

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.speech import VoiceCaption
from vrctranslate.domain.translation import TranslationRequest


class TranslateVoiceText:
    def __init__(self, translate_text: TranslateText) -> None:
        self._translate_text = translate_text

    def execute(
        self,
        original: str,
        detected_language: str,
        sequence: int,
        settings: AppSettings,
    ) -> VoiceCaption:
        route = settings.translation.voice_route
        source_language = (
            detected_language
            if route.source_language == "auto" and detected_language
            else route.source_language
        )
        result = self._translate_text.execute(
            TranslationRequest(
                request_id=f"voice-{uuid4().hex}",
                text=original,
                source_language=source_language,
                target_language=route.target_language,
                purpose="voice",
            ),
            settings.translation,
        )
        return VoiceCaption(
            sequence=sequence,
            original=original,
            translated=result.translated,
            detected_language=detected_language,
        )
