from __future__ import annotations

from collections import deque

from vrctranslate.application.dto import VoiceSegmentSettings
from vrctranslate.domain.speech import AudioFrame


class VoiceSegmenter:
    """Small in-memory energy VAD for short, bounded speech requests."""

    def __init__(self, settings: VoiceSegmentSettings) -> None:
        self._settings = settings
        self._pre_roll: deque[bytes] = deque()
        self._pre_roll_bytes = 0
        self._buffer: list[bytes] = []
        self._buffer_bytes = 0
        self._speech_bytes = 0
        self._silent_bytes = 0

    def feed(self, frame: AudioFrame) -> bytes | None:
        if frame.sample_rate != 16_000 or frame.channels != 1:
            raise ValueError("语音分段器只接受 16 kHz 单声道 PCM")
        data = frame.pcm16[: len(frame.pcm16) - len(frame.pcm16) % 2]
        if not data:
            return None
        samples = memoryview(data).cast("h")
        energy = sum(sample * sample for sample in samples)
        voice = energy >= (
            self._settings.energy_threshold
            * self._settings.energy_threshold
            * len(samples)
        )
        if not self._buffer:
            self._push_pre_roll(data)
            if not voice:
                return None
            self._buffer = list(self._pre_roll)
            self._buffer_bytes = sum(map(len, self._buffer))
            self._pre_roll.clear()
            self._pre_roll_bytes = 0

        if not self._buffer or self._buffer[-1] is not data:
            self._buffer.append(data)
            self._buffer_bytes += len(data)
        if voice:
            self._speech_bytes += len(data)
            self._silent_bytes = 0
        else:
            self._silent_bytes += len(data)

        if self._buffer_bytes >= self._bytes_for_seconds(
            self._settings.maximum_segment_seconds
        ):
            return self._finish()
        if (
            self._silent_bytes >= self._bytes_for_ms(self._settings.silence_ms)
            and self._speech_bytes
            >= self._bytes_for_ms(self._settings.minimum_speech_ms)
        ):
            return self._finish()
        return None

    def flush(self) -> bytes | None:
        if self._speech_bytes < self._bytes_for_ms(
            self._settings.minimum_speech_ms
        ):
            self.reset()
            return None
        return self._finish()

    def reset(self) -> None:
        self._pre_roll.clear()
        self._pre_roll_bytes = 0
        self._buffer.clear()
        self._buffer_bytes = 0
        self._speech_bytes = 0
        self._silent_bytes = 0

    def _finish(self) -> bytes | None:
        output = b"".join(self._buffer)
        self.reset()
        return output or None

    def _push_pre_roll(self, data: bytes) -> None:
        self._pre_roll.append(data)
        self._pre_roll_bytes += len(data)
        limit = self._bytes_for_ms(200)
        while self._pre_roll and self._pre_roll_bytes > limit:
            self._pre_roll_bytes -= len(self._pre_roll.popleft())

    @staticmethod
    def _bytes_for_ms(milliseconds: int) -> int:
        return int(16_000 * 2 * milliseconds / 1000)

    @staticmethod
    def _bytes_for_seconds(seconds: float) -> int:
        return int(16_000 * 2 * seconds)
