from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v3 import settings_v3_from_dict


def migrate_v2(raw: dict[str, Any]) -> AppSettings:
    """Map v2 into v3 while dropping the retired time-based duplicate setting."""

    migrated = dict(raw)
    ocr = dict(raw.get("ocr")) if isinstance(raw.get("ocr"), dict) else {}
    ocr.pop("duplicate_seconds", None)
    ocr["recognition_mode"] = "continuous"
    migrated["ocr"] = ocr
    settings = settings_v3_from_dict(migrated)
    if settings.translation.ocr_route.source_language == "auto":
        settings.translation.ocr_route.source_language = "ja"
    return settings
