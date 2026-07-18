from __future__ import annotations

import json
import shutil
from datetime import datetime
from pathlib import Path

from vrctranslate.application.dto import CONFIG_VERSION, AppSettings
from vrctranslate.infrastructure.paths import AppPaths, discover_app_paths
from vrctranslate.infrastructure.settings.migration_v1 import migrate_v1
from vrctranslate.infrastructure.settings.migration_v2 import migrate_v2
from vrctranslate.infrastructure.settings.migration_v3 import migrate_v3
from vrctranslate.infrastructure.settings.migration_v4 import migrate_v4
from vrctranslate.infrastructure.settings.migration_v5 import migrate_v5
from vrctranslate.infrastructure.settings.migration_v6 import migrate_v6
from vrctranslate.infrastructure.settings.schema_v3 import int_in_range
from vrctranslate.infrastructure.settings.schema_v7 import (
    settings_v7_from_dict,
    settings_v7_to_dict,
)


def default_config_path() -> Path:
    return discover_app_paths().config_file


class JsonSettingsRepository:
    """Atomic JSON persistence; schema mapping and migration live separately."""

    def __init__(
        self,
        path: Path | None = None,
        app_paths: AppPaths | None = None,
    ) -> None:
        self._app_paths = app_paths or discover_app_paths()
        self._path = path or self._app_paths.config_file
        self._legacy_path = (
            self._app_paths.application_root / "config.json" if path is None else None
        )

    @property
    def location(self) -> str:
        return str(self._path)

    def load(self) -> AppSettings:
        self._copy_legacy_config_if_needed()
        if not self._path.exists():
            settings = AppSettings()
            self.save(settings)
            return settings
        try:
            raw = json.loads(self._path.read_text(encoding="utf-8"))
            if not isinstance(raw, dict):
                raise ValueError("配置根节点必须是对象")
            version = int_in_range(raw.get("version"), 1, 1, CONFIG_VERSION)
            if version == 1:
                settings = migrate_v1(raw)
                self._backup_version(1)
                self.save(settings)
                return settings
            if version == 2:
                settings = migrate_v2(raw)
                self._backup_version(2)
                self.save(settings)
                return settings
            if version == 3:
                settings = migrate_v3(raw)
                self._backup_version(3)
                self.save(settings)
                return settings
            if version == 4:
                settings = migrate_v4(raw)
                self._backup_version(4)
                self.save(settings)
                return settings
            if version == 5:
                settings = migrate_v5(raw)
                self._backup_version(5)
                self.save(settings)
                return settings
            if version == 6:
                settings = migrate_v6(raw)
                self._backup_version(6)
                self.save(settings)
                return settings
            return settings_v7_from_dict(raw)
        except (OSError, ValueError, json.JSONDecodeError):
            timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
            broken = self._path.with_name(f"{self._path.name}.broken-{timestamp}")
            try:
                self._path.replace(broken)
            except OSError:
                pass
            settings = AppSettings()
            try:
                self.save(settings)
            except OSError:
                pass
            return settings

    def save(self, settings: AppSettings) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self._path.with_suffix(self._path.suffix + ".tmp")
        temporary.write_text(
            json.dumps(settings_v7_to_dict(settings), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        temporary.replace(self._path)

    def _copy_legacy_config_if_needed(self) -> None:
        legacy = self._legacy_path
        if (
            self._path.exists()
            or legacy is None
            or not legacy.exists()
            or legacy.resolve() == self._path.resolve()
        ):
            return
        self._path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(legacy, self._path)

    def _backup_version(self, version: int) -> None:
        backup = self._path.with_name(f"{self._path.name}.v{version}-backup")
        if not backup.exists():
            shutil.copy2(self._path, backup)
