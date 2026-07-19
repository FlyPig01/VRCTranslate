from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import CONFIG_VERSION, AppSettings
from vrctranslate.infrastructure.settings.schema_v9 import (
    settings_v9_from_dict,
    settings_v9_to_dict,
)


def settings_v10_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.voice.ensure_profiles()
    raw = settings_v9_to_dict(settings)
    raw["version"] = CONFIG_VERSION
    return raw


def settings_v10_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v9_from_dict(raw)
    for profile in settings.voice.asr_profiles:
        if profile.provider == "openai_compatible":
            profile.provider = (
                "dashscope_realtime"
                if "paraformer" in profile.model.casefold()
                else "openai_transcription"
            )
        if profile.provider == "dashscope_realtime":
            profile.base_url = ""
            if "openai" in profile.name.casefold():
                profile.name = "Paraformer 实时语音"
    settings.voice.ensure_profiles()
    settings.version = CONFIG_VERSION
    return settings
