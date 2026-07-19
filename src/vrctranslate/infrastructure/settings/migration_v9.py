from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.schema_v10 import settings_v10_from_dict


def migrate_v9(raw: dict[str, Any]) -> AppSettings:
    """Separate file transcription, realtime ASR, and audio-model protocols."""

    return settings_v10_from_dict(raw)
