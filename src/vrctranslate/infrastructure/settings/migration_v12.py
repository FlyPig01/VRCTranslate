from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v13 import settings_v13_from_dict


def migrate_v12(raw: dict[str, Any]) -> AppSettings:
    """Add disabled-by-default microphone translation settings."""

    return settings_v13_from_dict(raw)
