from __future__ import annotations

from collections.abc import Callable
from typing import Protocol

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.domain.speech import (
    AudioFrame,
    SpeechProfileValidationResult,
    SpeechRecognitionRequest,
    SpeechRecognitionResult,
    SpeechServiceCapabilities,
    SpeechStreamConfig,
    SpeechStreamEvent,
)


class SpeechStreamSession(Protocol):
    def push_audio(self, frame: AudioFrame) -> None: ...

    def close(self) -> None: ...

    def cancel(self) -> None: ...


class SpeechRecognizer(Protocol):
    def capabilities(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechServiceCapabilities: ...

    def open_session(
        self,
        profile: SpeechRecognitionProfile,
        config: SpeechStreamConfig,
        on_event: Callable[[SpeechStreamEvent], None],
        on_error: Callable[[Exception], None],
    ) -> SpeechStreamSession: ...

    def validate_profile(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechProfileValidationResult: ...

    def transcribe(
        self,
        request: SpeechRecognitionRequest,
        profile: SpeechRecognitionProfile,
    ) -> SpeechRecognitionResult: ...
