from __future__ import annotations

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.settings_repository import SettingsRepository


class ManageSettings:
    def __init__(self, repository: SettingsRepository) -> None:
        self._repository = repository
        self._current: AppSettings | None = None

    @property
    def current(self) -> AppSettings:
        if self._current is None:
            self._current = self._repository.load()
        return self._current

    @property
    def location(self) -> str:
        return self._repository.location

    def load(self) -> AppSettings:
        self._current = self._repository.load()
        return self._current

    def save(self, settings: AppSettings) -> None:
        self._repository.save(settings)
        self._current = settings

