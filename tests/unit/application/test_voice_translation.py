from __future__ import annotations

from vrctranslate.application.dto import (
    AppSettings,
    SpeechRecognitionProfile,
    VoiceSegmentSettings,
)
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.application.use_cases.translate_voice_segment import (
    TranslateVoiceSegment,
)
from vrctranslate.application.use_cases.voice_segmenter import VoiceSegmenter
from vrctranslate.application.use_cases.voice_activity_gate import VoiceActivityGate
from vrctranslate.domain.speech import (
    AudioFrame,
    SpeechRecognitionResult,
)
from vrctranslate.domain.translation import TranslationResult


def _pcm(value: int, milliseconds: int = 100) -> bytes:
    return int(value).to_bytes(2, "little", signed=True) * (16 * milliseconds)


def test_voice_segmenter_ignores_silence_and_emits_bounded_speech() -> None:
    segmenter = VoiceSegmenter(
        VoiceSegmentSettings(
            energy_threshold=300,
            silence_ms=600,
            minimum_speech_ms=300,
            maximum_segment_seconds=12,
        )
    )

    for _ in range(5):
        assert segmenter.feed(AudioFrame(_pcm(0))) is None
    for _ in range(4):
        assert segmenter.feed(AudioFrame(_pcm(2000))) is None
    result = None
    for _ in range(6):
        result = segmenter.feed(AudioFrame(_pcm(0)))

    assert result is not None
    assert len(result) <= 32_000 * 2
    assert segmenter.flush() is None


def test_voice_segmenter_does_not_submit_background_silence() -> None:
    segmenter = VoiceSegmenter(VoiceSegmentSettings())

    for _ in range(100):
        assert segmenter.feed(AudioFrame(_pcm(0))) is None

    assert segmenter.flush() is None


def test_adaptive_microphone_vad_detects_quiet_speech_below_legacy_threshold() -> None:
    segmenter = VoiceSegmenter(
        VoiceSegmentSettings(
            energy_threshold=350,
            silence_ms=600,
            minimum_speech_ms=300,
            maximum_segment_seconds=12,
        ),
        adaptive_noise=True,
        calibration_ms=500,
    )

    for _ in range(5):
        assert segmenter.feed(AudioFrame(_pcm(20))) is None
    for _ in range(4):
        assert segmenter.feed(AudioFrame(_pcm(180))) is None
    result = None
    for _ in range(6):
        result = segmenter.feed(AudioFrame(_pcm(20)))

    assert result is not None


def test_adaptive_microphone_vad_finalizes_one_short_voice_frame() -> None:
    segmenter = VoiceSegmenter(
        VoiceSegmentSettings(
            energy_threshold=350,
            silence_ms=600,
            minimum_speech_ms=300,
            maximum_segment_seconds=12,
        ),
        adaptive_noise=True,
        calibration_ms=500,
    )

    for _ in range(5):
        assert segmenter.feed(AudioFrame(_pcm(20))) is None
    assert segmenter.feed(AudioFrame(_pcm(800))) is None
    result = None
    for _ in range(6):
        result = segmenter.feed(AudioFrame(_pcm(20)))

    assert result is not None


def test_rejected_short_noise_does_not_join_the_next_sentence() -> None:
    segmenter = VoiceSegmenter(
        VoiceSegmentSettings(
            energy_threshold=300,
            silence_ms=600,
            minimum_speech_ms=300,
            maximum_segment_seconds=12,
        )
    )

    assert segmenter.feed(AudioFrame(_pcm(1000))) is None
    for _ in range(6):
        assert segmenter.feed(AudioFrame(_pcm(0))) is None
    for _ in range(3):
        assert segmenter.feed(AudioFrame(_pcm(2000))) is None
    result = None
    for _ in range(6):
        result = segmenter.feed(AudioFrame(_pcm(0)))

    assert result is not None
    assert result.count(int(1000).to_bytes(2, "little", signed=True)) == 0


def test_voice_activity_gate_forwards_during_speech_without_waiting_for_sentence() -> None:
    gate = VoiceActivityGate(
        VoiceSegmentSettings(
            energy_threshold=300,
            silence_ms=600,
            minimum_speech_ms=300,
            maximum_segment_seconds=12,
        )
    )

    assert gate.feed(AudioFrame(_pcm(0))) == ()
    assert gate.feed(AudioFrame(_pcm(2000))) == ()
    assert gate.feed(AudioFrame(_pcm(2000))) == ()
    started = gate.feed(AudioFrame(_pcm(2000)))
    following = gate.feed(AudioFrame(_pcm(2000)))

    assert started
    assert following
    assert sum(frame.duration_seconds for frame in started) <= 0.5


def test_voice_segment_runs_asr_then_voice_translation_without_osc() -> None:
    class Recognizer:
        def transcribe(self, request, profile):
            assert request.source_language == "auto"
            assert request.pcm16 == b"\x01\x00" * 1600
            assert profile.id == "speech-test"
            return SpeechRecognitionResult(request.request_id, "konnichiwa", "ja")

    class Translator:
        def __init__(self) -> None:
            self.request = None

        def translate(self, request, profile):
            self.request = request
            return TranslationResult(
                request.request_id,
                request.text,
                "你好",
                request.source_language,
                request.target_language,
                request.purpose,
            )

    translator = Translator()
    use_case = TranslateVoiceSegment(Recognizer(), TranslateText(translator))
    settings = AppSettings()
    settings.voice.asr_profiles = [
        SpeechRecognitionProfile(
            id="speech-test",
            name="Test speech",
            provider="tencent_realtime",
            api_key="secret-key",
            model="16k_zh",
            options={"app_id": "123", "secret_id": "secret-id"},
        )
    ]
    settings.voice.asr_profile_id = "speech-test"

    caption = use_case.execute(b"\x01\x00" * 1600, 7, settings)

    assert caption.sequence == 7
    assert caption.original == "konnichiwa"
    assert caption.translated == "你好"
    assert translator.request.purpose == "voice"
    assert translator.request.source_language == "ja"
