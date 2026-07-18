from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict
from typing import Any

from vrctranslate.application.dto import CONFIG_VERSION, AppSettings
from vrctranslate.infrastructure.settings.schema_v5 import settings_v5_from_dict


def _profiles(raw: dict[str, Any]) -> list[dict[str, Any]]:
    translation = raw.get("translation")
    if not isinstance(translation, dict):
        return []
    profiles = translation.get("profiles")
    if not isinstance(profiles, list):
        return []
    return [profile for profile in profiles if isinstance(profile, dict)]


def settings_v6_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.version = CONFIG_VERSION
    settings.translation.ensure_routes()
    raw = asdict(settings)
    for profile in _profiles(raw):
        if profile.get("provider") != "tencent":
            continue
        profile["secret_id"] = profile.pop("api_key", "")
        profile["secret_key"] = profile.pop("model", "")
    return raw


def settings_v6_from_dict(raw: dict[str, Any]) -> AppSettings:
    compatible = deepcopy(raw)
    for profile in _profiles(compatible):
        if profile.get("provider") != "tencent":
            continue
        profile["api_key"] = profile.get("secret_id", profile.get("api_key", ""))
        profile["model"] = profile.get("secret_key", profile.get("model", ""))
    settings = settings_v5_from_dict(compatible)
    settings.version = CONFIG_VERSION
    return settings
