from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    AppSettings,
)
from vrctranslate.infrastructure.settings.schema_v11 import (
    settings_v11_from_dict,
    settings_v11_to_dict,
)


def settings_v12_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.translation.voice_route.translation_strategy = "text_profile"
    settings.voice.ensure_profiles()
    raw = settings_v11_to_dict(settings)
    raw.setdefault("ocr", {})["region_coordinate_space"] = (
        settings.ocr.region_coordinate_space
        if settings.ocr.region_coordinate_space in {"window", "screen"}
        else "window"
    )
    raw["version"] = CONFIG_VERSION
    return raw


def settings_v12_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v11_from_dict(raw)
    ocr = raw.get("ocr") if isinstance(raw.get("ocr"), dict) else {}
    coordinate_space = str(ocr.get("region_coordinate_space", "window"))
    settings.ocr.region_coordinate_space = (
        coordinate_space if coordinate_space in {"window", "screen"} else "window"
    )
    settings.translation.voice_route.translation_strategy = "text_profile"
    settings.voice.ensure_profiles()
    settings.translation.ensure_routes()
    settings.version = CONFIG_VERSION
    return settings
