from __future__ import annotations

from array import array
from collections import deque

from vrctranslate.application.dto import VoiceSegmentSettings
from vrctranslate.domain.speech import AudioFrame


class VoiceActivityGate:
    """Forward speech immediately while retaining a short in-memory pre-roll."""

    def __init__(self, settings: VoiceSegmentSettings) -> None:
        self._settings = settings
        self._pre_roll: deque[AudioFrame] = deque()
        self._pre_roll_seconds = 0.0
        self._active = False
        self._candidate_speech_seconds = 0.0
        self._silence_seconds = 0.0
        self._keepalive_seconds = 0.0

    def feed(self, frame: AudioFrame) -> tuple[AudioFrame, ...]:
        duration = frame.duration_seconds
        energy = _mean_absolute_amplitude(frame.pcm16)
        speech = energy >= self._settings.energy_threshold
        if not self._active:
            self._append_pre_roll(frame)
            self._keepalive_seconds += duration
            self._candidate_speech_seconds = (
                self._candidate_speech_seconds + duration if speech else 0.0
            )
            if (
                self._candidate_speech_seconds * 1000
                < self._settings.minimum_speech_ms
            ):
                if self._keepalive_seconds >= 1.0:
                    self._keepalive_seconds = 0.0
                    return (frame,)
                return ()
            self._active = True
            self._candidate_speech_seconds = 0.0
            self._keepalive_seconds = 0.0
            self._silence_seconds = 0.0
            frames = tuple(self._pre_roll)
            self._pre_roll.clear()
            self._pre_roll_seconds = 0.0
            return frames

        if speech:
            self._silence_seconds = 0.0
        else:
            self._silence_seconds += duration
        if self._silence_seconds * 1000 >= self._settings.silence_ms:
            self._active = False
            self._silence_seconds = 0.0
        return (frame,)

    def reset(self) -> None:
        self._pre_roll.clear()
        self._pre_roll_seconds = 0.0
        self._active = False
        self._candidate_speech_seconds = 0.0
        self._silence_seconds = 0.0
        self._keepalive_seconds = 0.0

    def _append_pre_roll(self, frame: AudioFrame) -> None:
        self._pre_roll.append(frame)
        self._pre_roll_seconds += frame.duration_seconds
        maximum = max(0.25, self._settings.minimum_speech_ms / 1000 + 0.1)
        while self._pre_roll and self._pre_roll_seconds > maximum:
            removed = self._pre_roll.popleft()
            self._pre_roll_seconds -= removed.duration_seconds


def _mean_absolute_amplitude(pcm16: bytes) -> int:
    if not pcm16:
        return 0
    samples = array("h")
    samples.frombytes(pcm16[: len(pcm16) - len(pcm16) % 2])
    if not samples:
        return 0
    return int(sum(abs(value) for value in samples) / len(samples))
