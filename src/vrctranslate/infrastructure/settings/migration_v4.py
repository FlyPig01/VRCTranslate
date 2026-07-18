from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v4 import settings_v4_from_dict


def migrate_v4(raw: dict[str, Any]) -> AppSettings:
    """Replace the legacy romaji boolean with explicit route modes."""

    return settings_v4_from_dict(raw)
