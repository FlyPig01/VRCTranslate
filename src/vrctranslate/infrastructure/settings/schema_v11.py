from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import CONFIG_VERSION, AppSettings
from vrctranslate.application.speech_profiles import (
    profile_validation_state,
    set_profile_validation,
)
from vrctranslate.infrastructure.settings.schema_v10 import (
    settings_v10_from_dict,
    settings_v10_to_dict,
)


def settings_v11_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.voice.ensure_profiles()
    raw = settings_v10_to_dict(settings)
    raw["version"] = CONFIG_VERSION
    return raw


def settings_v11_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v10_from_dict(raw)
    settings.translation.voice_route.translation_strategy = "text_profile"
    for profile in settings.voice.asr_profiles:
        state = profile_validation_state(profile)
        if state == "incompatible":
            set_profile_validation(
                profile,
                "incompatible",
                "该档案属于旧语音协议，当前版本不支持启动",
            )
        elif state not in {"verified", "failed"}:
            set_profile_validation(profile, "pending", "")
    settings.voice.ensure_profiles()
    settings.translation.ensure_routes()
    settings.version = CONFIG_VERSION
    return settings
