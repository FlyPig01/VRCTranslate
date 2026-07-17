from __future__ import annotations

from typing import Protocol

from vrctranslate.application.dto import AppSettings


class SettingsRepository(Protocol):
    @property
    def location(self) -> str: ...

    def load(self) -> AppSettings: ...

    def save(self, settings: AppSettings) -> None: ...

