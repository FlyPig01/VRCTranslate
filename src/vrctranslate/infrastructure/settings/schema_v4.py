from __future__ import annotations

from dataclasses import asdict
from typing import Any

from vrctranslate.application.dto import CONFIG_VERSION, AppSettings
from vrctranslate.infrastructure.settings.schema_v3 import (
    float_in_range,
    mapping,
    settings_v3_from_dict,
)


def settings_v4_to_dict(settings: AppSettings) -> dict[str, Any]:
    settings.version = CONFIG_VERSION
    settings.translation.ensure_routes()
    return asdict(settings)


def settings_v4_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v3_from_dict(raw)
    if settings.translation.ocr_route.source_language not in {"zh-CN", "ja", "en"}:
        settings.translation.ocr_route.source_language = "ja"
    ui = mapping(raw.get("ui"))
    mode = str(ui.get("ocr_display_mode", "overlay"))
    settings.ui.ocr_display_mode = (
        mode if mode in {"overlay", "inline", "both"} else "overlay"
    )
    settings.ui.ocr_inline_opacity = float_in_range(
        ui.get("ocr_inline_opacity"), 0.9, 0.5, 1.0
    )
    settings.ui.ocr_inline_auto_contrast = bool(
        ui.get("ocr_inline_auto_contrast", True)
    )
    settings.version = CONFIG_VERSION
    return settings
