from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v12 import settings_v12_from_dict


def migrate_v11(raw: dict[str, Any]) -> AppSettings:
    """Remove obsolete speech protocols and normalize to the three ASR providers."""

    return settings_v12_from_dict(raw)
