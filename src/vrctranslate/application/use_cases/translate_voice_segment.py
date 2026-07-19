from __future__ import annotations

from uuid import uuid4

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.speech_recognizer import SpeechRecognizer
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.speech import (
    SpeechRecognitionRequest,
    VoiceCaption,
)
from vrctranslate.domain.translation import TranslationRequest


class TranslateVoiceSegment:
    def __init__(
        self,
        recognizer: SpeechRecognizer,
        translate_text: TranslateText,
    ) -> None:
        self._recognizer = recognizer
        self._translate_text = translate_text

    def execute(
        self,
        pcm16: bytes,
        sequence: int,
        settings: AppSettings,
    ) -> VoiceCaption:
        route = settings.translation.voice_route
        request_id = f"voice-{uuid4().hex}"
        recognition = self._recognizer.transcribe(
            SpeechRecognitionRequest(
                request_id=request_id,
                pcm16=pcm16,
                sample_rate=16_000,
                source_language=route.source_language,
            ),
            settings.voice.asr_profile(),
        )
        original = recognition.text.strip()
        if not original:
            raise ValueError("语音识别服务返回了空文本")
        source_language = (
            recognition.detected_language
            if route.source_language == "auto" and recognition.detected_language
            else route.source_language
        )
        translated = self._translate_text.execute(
            TranslationRequest(
                request_id=request_id,
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
            translated=translated.translated,
            detected_language=recognition.detected_language,
        )
