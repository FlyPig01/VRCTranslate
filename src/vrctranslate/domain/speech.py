from __future__ import annotations

from dataclasses import dataclass
from typing import Literal


@dataclass(frozen=True, slots=True)
class AudioFrame:
    pcm16: bytes
    sample_rate: int = 16_000
    channels: int = 1

    @property
    def duration_seconds(self) -> float:
        bytes_per_second = self.sample_rate * self.channels * 2
        return len(self.pcm16) / bytes_per_second if bytes_per_second else 0.0


@dataclass(frozen=True, slots=True)
class SpeechRecognitionRequest:
    request_id: str
    pcm16: bytes
    sample_rate: int
    source_language: str


@dataclass(frozen=True, slots=True)
class SpeechRecognitionResult:
    request_id: str
    text: str
    detected_language: str = ""


@dataclass(frozen=True, slots=True)
class SpeechServiceCapabilities:
    provider: str
    streaming_audio: bool
    partial_transcript: bool
    final_transcript: bool
    source_language_auto: bool = True
    deployment: Literal["local", "cloud"] = "cloud"

    @property
    def realtime_eligible(self) -> bool:
        return self.streaming_audio and (
            self.partial_transcript or self.final_transcript
        )


@dataclass(frozen=True, slots=True)
class SpeechStreamConfig:
    source_language: str = "auto"
    target_language: str = "zh-CN"


@dataclass(frozen=True, slots=True)
class SpeechStreamEvent:
    kind: Literal[
        "partial_transcript",
        "final_transcript",
    ]
    text: str
    utterance_id: str = "current"
    detected_language: str = ""


@dataclass(frozen=True, slots=True)
class SpeechProfileValidationResult:
    state: Literal["verified", "failed", "incompatible"]
    message: str


@dataclass(frozen=True, slots=True)
class VoiceCaption:
    sequence: int
    original: str
    translated: str
    detected_language: str = ""


class ProcessAudioCaptureError(RuntimeError):
    """A process-output capture failure safe to report at the UI boundary."""


class SpeechRecognitionError(RuntimeError):
    def __init__(self, category: str, user_message: str) -> None:
        super().__init__(user_message)
        self.category = category
        self.user_message = user_message
