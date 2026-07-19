from __future__ import annotations

from uuid import uuid4

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.speech_recognizer import SpeechRecognizer
from vrctranslate.domain.speech import SpeechRecognitionRequest, SpeechRecognitionResult


class RecognizeVoiceSegment:
    """Recognize one completed in-memory PCM segment without retaining audio."""

    def __init__(self, recognizer: SpeechRecognizer) -> None:
        self._recognizer = recognizer

    def execute(self, pcm16: bytes, settings: AppSettings) -> SpeechRecognitionResult:
        route = settings.translation.voice_route
        return self._recognizer.transcribe(
            SpeechRecognitionRequest(
                request_id=f"voice-{uuid4().hex}",
                pcm16=pcm16,
                sample_rate=16_000,
                source_language=route.source_language,
            ),
            settings.voice.asr_profile(),
        )
