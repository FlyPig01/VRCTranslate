from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v7 import settings_v7_from_dict


def migrate_v6(raw: dict[str, Any]) -> AppSettings:
    """Enable local glossary defaults without creating remote resources."""

    return settings_v7_from_dict(raw)
