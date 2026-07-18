from __future__ import annotations

from dataclasses import asdict
from typing import Any

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    ROMAJI_MODES,
    AppSettings,
)
from vrctranslate.infrastructure.settings.schema_v3 import mapping
from vrctranslate.infrastructure.settings.schema_v4 import settings_v4_from_dict


def settings_v5_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.version = CONFIG_VERSION
    settings.translation.ensure_routes()
    return asdict(settings)


def _route_mode(raw: dict[str, Any], default: str) -> str:
    mode = str(raw.get("romaji_mode", ""))
    if mode in ROMAJI_MODES:
        return mode
    if "romaji_to_kana" in raw:
        return "auto" if bool(raw.get("romaji_to_kana")) else "off"
    return default


def settings_v5_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v4_from_dict(raw)
    translation = mapping(raw.get("translation"))
    settings.translation.self_route.romaji_mode = _route_mode(
        mapping(translation.get("self_route")),
        "auto",
    )
    settings.translation.ocr_route.romaji_mode = _route_mode(
        mapping(translation.get("ocr_route")),
        "off",
    )
    settings.version = CONFIG_VERSION
    return settings
