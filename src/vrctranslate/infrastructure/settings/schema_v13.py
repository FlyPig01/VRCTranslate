from __future__ import annotations

from dataclasses import asdict
from typing import Any

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    SELF_VOICE_ACTIVATION_SCOPES,
    SELF_VOICE_SOURCE_LANGUAGES,
    AppSettings,
    SelfVoiceSettings,
    VoiceSegmentSettings,
)
from vrctranslate.infrastructure.settings.schema_v3 import (
    float_in_range,
    int_in_range,
    mapping,
)
from vrctranslate.infrastructure.settings.schema_v12 import (
    settings_v12_from_dict,
    settings_v12_to_dict,
)


def settings_v13_to_dict(settings: AppSettings) -> dict[str, Any]:
    raw = settings_v12_to_dict(settings)
    raw["self_voice"] = asdict(settings.self_voice)
    raw["version"] = CONFIG_VERSION
    return raw


def settings_v13_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v12_from_dict(raw)
    self_voice = mapping(raw.get("self_voice"))
    segment = mapping(self_voice.get("segment"))
    source_language = str(self_voice.get("source_language", "zh-CN"))
    activation_scope = str(
        self_voice.get("activation_scope", "vrchat_foreground")
    )
    settings.self_voice = SelfVoiceSettings(
        enabled=bool(self_voice.get("enabled", False)),
        microphone_id=str(self_voice.get("microphone_id", "")),
        source_language=(
            source_language
            if source_language in SELF_VOICE_SOURCE_LANGUAGES
            else "zh-CN"
        ),
        activation_scope=(
            activation_scope
            if activation_scope in SELF_VOICE_ACTIVATION_SCOPES
            else "vrchat_foreground"
        ),
        toggle_hotkey=str(self_voice.get("toggle_hotkey", "Ctrl+Alt+M")),
        queue_limit=int_in_range(self_voice.get("queue_limit"), 2, 1, 4),
        segment=VoiceSegmentSettings(
            energy_threshold=int_in_range(
                segment.get("energy_threshold"), 120, 50, 10000
            ),
            silence_ms=int_in_range(segment.get("silence_ms"), 650, 300, 1500),
            minimum_speech_ms=int_in_range(
                segment.get("minimum_speech_ms"), 100, 100, 2000
            ),
            maximum_segment_seconds=float_in_range(
                segment.get("maximum_segment_seconds"), 12.0, 2.0, 20.0
            ),
        ),
    )
    settings.version = CONFIG_VERSION
    return settings
