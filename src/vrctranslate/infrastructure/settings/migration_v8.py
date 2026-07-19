from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v9 import settings_v9_from_dict


def migrate_v8(raw: dict[str, Any]) -> AppSettings:
    """Add disabled-by-default process voice translation settings."""

    return settings_v9_from_dict(raw)
