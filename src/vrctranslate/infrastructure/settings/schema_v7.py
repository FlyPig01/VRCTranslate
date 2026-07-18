from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    AppSettings,
    GlossarySettings,
)
from vrctranslate.infrastructure.settings.schema_v3 import mapping
from vrctranslate.infrastructure.settings.schema_v6 import (
    settings_v6_from_dict,
    settings_v6_to_dict,
)


def settings_v7_to_dict(settings: AppSettings) -> dict[str, Any]:
    return settings_v6_to_dict(settings)


def settings_v7_from_dict(raw: dict[str, Any]) -> AppSettings:
    settings = settings_v6_from_dict(raw)
    glossary = mapping(raw.get("glossary"))
    settings.glossary = GlossarySettings(
        enabled=bool(glossary.get("enabled", True)),
        builtin_enabled=bool(glossary.get("builtin_enabled", True)),
    )
    settings.version = CONFIG_VERSION
    return settings
