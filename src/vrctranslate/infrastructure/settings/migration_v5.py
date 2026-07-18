from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v6 import settings_v6_from_dict


def migrate_v5(raw: dict[str, Any]) -> AppSettings:
    """Rename Tencent credential keys without changing their values."""

    return settings_v6_from_dict(raw)
