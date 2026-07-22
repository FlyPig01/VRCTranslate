from __future__ import annotations

from uuid import uuid4

from vrctranslate.application.dto import AppSettings, SpeechRecognitionProfile
from vrctranslate.application.ports.speech_recognizer import SpeechRecognizer
from vrctranslate.domain.speech import SpeechRecognitionRequest, SpeechRecognitionResult


def self_voice_profile() -> SpeechRecognitionProfile:
    return SpeechRecognitionProfile(
        id="self-voice-local",
        name="SenseVoiceSmall INT8",
        provider="local_offline",
        model="sensevoice-small-int8",
        options={"validation_state": "verified"},
    )


class RecognizeSelfVoiceSegment:
    """Recognize one microphone sentence with the portable local model."""

    def __init__(self, recognizer: SpeechRecognizer) -> None:
        self._recognizer = recognizer

    def execute(
        self, pcm16: bytes, settings: AppSettings
    ) -> SpeechRecognitionResult:
        return self._recognizer.transcribe(
            SpeechRecognitionRequest(
                request_id=f"self-voice-{uuid4().hex}",
                pcm16=pcm16,
                sample_rate=16_000,
                source_language=settings.self_voice.source_language,
            ),
            self_voice_profile(),
        )
