from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    LEGACY_SPEECH_RECOGNITION_PROVIDERS,
    AppSettings,
    SpeechRecognitionProfile,
    SUPPORTED_SPEECH_RECOGNITION_PROVIDERS,
    TranslationRouteSettings,
    VoiceOverlaySettings,
    VoiceSegmentSettings,
    VoiceTranslationSettings,
)
from vrctranslate.infrastructure.settings.schema_v3 import (
    float_in_range,
    int_in_range,
    mapping,
    route_from_dict,
)
from vrctranslate.infrastructure.settings.schema_v8 import (
    settings_v8_from_dict,
    settings_v8_to_dict,
)


def settings_v9_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.voice.ensure_profiles()
    raw = settings_v8_to_dict(settings)
    raw["version"] = CONFIG_VERSION
    return raw


def settings_v9_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v8_from_dict(raw)
    translation = mapping(raw.get("translation"))
    settings.translation.voice_route = route_from_dict(
        mapping(translation.get("voice_route")),
        TranslationRouteSettings(
            source_language="auto",
            target_language="zh-CN",
            timeout_seconds=8.0,
            queue_limit=2,
            task_ttl_seconds=20.0,
            romaji_mode="auto",
        ),
    )

    voice = mapping(raw.get("voice"))
    profiles: list[SpeechRecognitionProfile] = []
    raw_profiles = voice.get("asr_profiles")
    if isinstance(raw_profiles, list):
        for index, item in enumerate(raw_profiles):
            if not isinstance(item, dict):
                continue
            profile_id = str(item.get("id", f"speech-{index + 1}")).strip()
            if not profile_id:
                profile_id = f"speech-{index + 1}"
            provider = str(item.get("provider", "openai_transcription"))
            if provider not in (
                SUPPORTED_SPEECH_RECOGNITION_PROVIDERS
                | LEGACY_SPEECH_RECOGNITION_PROVIDERS
                | {"openai_compatible"}
            ):
                continue
            if provider == "openai_compatible":
                provider = (
                    "dashscope_realtime"
                    if "paraformer" in str(item.get("model", "")).casefold()
                    else "openai_transcription"
                )
            options = item.get("options") if isinstance(item.get("options"), dict) else {}
            profiles.append(
                SpeechRecognitionProfile(
                    id=profile_id,
                    name=str(item.get("name", profile_id)),
                    provider=provider,
                    base_url=str(item.get("base_url", "")),
                    api_key=str(item.get("api_key", "")),
                    model=str(item.get("model", "")),
                    timeout_seconds=float_in_range(
                        item.get("timeout_seconds"), 8.0, 8.0, 120.0
                    ),
                    options=dict(options),
                )
            )
    segment = mapping(voice.get("segment"))
    overlay = mapping(voice.get("overlay"))
    legacy_show_original = bool(overlay.get("show_original", True))
    display_mode = str(
        overlay.get(
            "display_mode",
            "both" if legacy_show_original else "translation",
        )
    )
    if display_mode not in {"translation", "original", "both"}:
        display_mode = "both" if legacy_show_original else "translation"
    settings.voice = VoiceTranslationSettings(
        target_process_name=str(voice.get("target_process_name", "VRChat.exe")),
        target_window_title=str(voice.get("target_window_title", "")),
        asr_profile_id=str(voice.get("asr_profile_id", "")),
        asr_profiles=profiles,
        segment=VoiceSegmentSettings(
            energy_threshold=int_in_range(
                segment.get("energy_threshold"), 350, 50, 10000
            ),
            silence_ms=int_in_range(segment.get("silence_ms"), 650, 200, 3000),
            minimum_speech_ms=int_in_range(
                segment.get("minimum_speech_ms"), 300, 100, 3000
            ),
            maximum_segment_seconds=float_in_range(
                segment.get("maximum_segment_seconds"), 12.0, 2.0, 30.0
            ),
        ),
        overlay=VoiceOverlaySettings(
            topmost=bool(overlay.get("topmost", True)),
            show_original=display_mode in {"original", "both"},
            display_mode=display_mode,
            x=int_in_range(overlay.get("x"), -1, -100000, 100000),
            y=int_in_range(overlay.get("y"), -1, -100000, 100000),
            width=int_in_range(overlay.get("width"), 560, 320, 1600),
            height=int_in_range(overlay.get("height"), 210, 100, 1000),
            opacity=float_in_range(overlay.get("opacity"), 0.9, 0.25, 1.0),
            font_size=int_in_range(overlay.get("font_size"), 18, 10, 40),
            max_items=int_in_range(overlay.get("max_items"), 3, 1, 10),
        ),
    )
    settings.voice.ensure_profiles()
    settings.translation.ensure_routes()
    settings.version = CONFIG_VERSION
    return settings
