from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v4 import settings_v4_from_dict


def migrate_v3(raw: dict[str, Any]) -> AppSettings:
    """Add optional inline rendering while preserving the old floating default."""

    migrated = dict(raw)
    translation = (
        dict(raw.get("translation"))
        if isinstance(raw.get("translation"), dict)
        else {}
    )
    ocr_route = (
        dict(translation.get("ocr_route"))
        if isinstance(translation.get("ocr_route"), dict)
        else {}
    )
    if ocr_route.get("source_language") == "auto":
        ocr_route["source_language"] = "ja"
    translation["ocr_route"] = ocr_route
    migrated["translation"] = translation
    ui = dict(raw.get("ui")) if isinstance(raw.get("ui"), dict) else {}
    ui.setdefault("ocr_display_mode", "overlay")
    ui.setdefault("ocr_inline_opacity", 0.9)
    ui.setdefault("ocr_inline_auto_contrast", True)
    migrated["ui"] = ui
    return settings_v4_from_dict(migrated)
