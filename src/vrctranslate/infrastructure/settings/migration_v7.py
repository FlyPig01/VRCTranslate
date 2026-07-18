from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v8 import settings_v8_from_dict


def migrate_v7(raw: dict[str, Any]) -> AppSettings:
    """Drop timed OCR overlay expiry and add multimodal-safe defaults."""

    return settings_v8_from_dict(raw)
