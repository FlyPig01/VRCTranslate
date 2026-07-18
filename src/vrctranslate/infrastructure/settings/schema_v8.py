from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings, CONFIG_VERSION
from vrctranslate.infrastructure.settings.schema_v7 import (
    settings_v7_from_dict,
    settings_v7_to_dict,
)


def settings_v8_to_dict(settings: AppSettings) -> dict[str, Any]:
    raw = settings_v7_to_dict(settings)
    raw["version"] = CONFIG_VERSION
    ui = raw.get("ui")
    if isinstance(ui, dict):
        ui.pop("ocr_overlay_display_seconds", None)
    return raw


def settings_v8_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v7_from_dict(raw)
    settings.version = CONFIG_VERSION
    return settings
