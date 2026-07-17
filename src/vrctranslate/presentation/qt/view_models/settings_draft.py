from __future__ import annotations

from copy import deepcopy

from vrctranslate.application.dto import AppSettings


class SettingsDraft:
    """Own a detached settings snapshot and its unsaved state."""

    def __init__(self) -> None:
        self._settings = AppSettings()
        self.dirty = False

    @property
    def settings(self) -> AppSettings:
        return self._settings

    def load(self, settings: AppSettings) -> None:
        self._settings = deepcopy(settings)
        self.dirty = False

    def replace(self, settings: AppSettings) -> None:
        self._settings = deepcopy(settings)

    def mark_dirty(self) -> None:
        self.dirty = True

    def mark_saved(self) -> None:
        self.dirty = False
