from __future__ import annotations

from collections import deque
from math import sqrt

from vrctranslate.application.dto import VoiceSegmentSettings
from vrctranslate.domain.speech import AudioFrame


class VoiceSegmenter:
    """Small in-memory energy VAD for short, bounded speech requests."""

    def __init__(
        self,
        settings: VoiceSegmentSettings,
        *,
        adaptive_noise: bool = False,
        calibration_ms: int = 0,
    ) -> None:
        self._settings = settings
        self._adaptive_noise = adaptive_noise
        self._calibration_target_bytes = self._bytes_for_ms(calibration_ms)
        self._calibration_bytes = 0
        self._calibration_levels: list[float] = []
        self._noise_floor = 0.0
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
        amplitude = sqrt(energy / len(samples))
        if self.calibrating:
            self._calibration_levels.append(amplitude)
            self._calibration_bytes += len(data)
            self._push_pre_roll(data)
            if not self.calibrating:
                ordered = sorted(self._calibration_levels)
                # A lower quartile ignores a few speech frames if the user
                # starts talking slightly before the short calibration ends.
                self._noise_floor = ordered[max(0, len(ordered) // 4)]
            return None
        threshold = float(self._settings.energy_threshold)
        if self._adaptive_noise:
            # Older v13 test configurations used 350. The adaptive microphone
            # mode intentionally caps that legacy baseline at 120.
            baseline = min(threshold, 120.0)
            threshold = max(baseline, self._noise_floor * 2.5 + 40.0)
        voice = amplitude >= threshold
        if self._adaptive_noise and not self._buffer and not voice:
            if self._noise_floor <= 0:
                self._noise_floor = amplitude
            else:
                self._noise_floor = self._noise_floor * 0.98 + amplitude * 0.02
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
        ):
            if self._speech_bytes >= self._minimum_speech_bytes():
                return self._finish()
            # A rejected click/noise must not remain buffered and merge with
            # the user's next sentence.
            self.reset()
        return None

    def flush(self) -> bytes | None:
        if self._speech_bytes < self._minimum_speech_bytes():
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

    @property
    def calibrating(self) -> bool:
        return self._calibration_bytes < self._calibration_target_bytes

    def _minimum_speech_bytes(self) -> int:
        minimum_ms = self._settings.minimum_speech_ms
        if self._adaptive_noise:
            # v13 configurations created during early testing stored 300 ms.
            # Microphone mode accepts a single 100 ms frame so short replies
            # such as “yes” are finalized instead of joining the next phrase.
            minimum_ms = min(minimum_ms, 100)
        return self._bytes_for_ms(minimum_ms)

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
