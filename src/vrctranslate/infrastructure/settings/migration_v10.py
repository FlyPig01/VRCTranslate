from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v11 import settings_v11_from_dict


def migrate_v10(raw: dict[str, Any]) -> AppSettings:
    """Preserve legacy speech profiles while enforcing realtime eligibility."""

    return settings_v11_from_dict(raw)
