from __future__ import annotations

import json
import shutil
from datetime import datetime
from pathlib import Path

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    AppSettings,
)
from vrctranslate.infrastructure.paths import AppPaths, discover_app_paths
from vrctranslate.infrastructure.settings.migration_v1 import migrate_v1
from vrctranslate.infrastructure.settings.migration_v2 import migrate_v2
from vrctranslate.infrastructure.settings.migration_v3 import migrate_v3
from vrctranslate.infrastructure.settings.migration_v4 import migrate_v4
from vrctranslate.infrastructure.settings.migration_v5 import migrate_v5
from vrctranslate.infrastructure.settings.migration_v6 import migrate_v6
from vrctranslate.infrastructure.settings.migration_v7 import migrate_v7
from vrctranslate.infrastructure.settings.migration_v8 import migrate_v8
from vrctranslate.infrastructure.settings.migration_v9 import migrate_v9
from vrctranslate.infrastructure.settings.migration_v10 import migrate_v10
from vrctranslate.infrastructure.settings.migration_v11 import migrate_v11
from vrctranslate.infrastructure.settings.migration_v12 import migrate_v12
from vrctranslate.infrastructure.settings.schema_v3 import int_in_range
from vrctranslate.infrastructure.settings.schema_v13 import (
    settings_v13_from_dict,
    settings_v13_to_dict,
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
            if version == 7:
                settings = migrate_v7(raw)
                self._backup_version(7)
                self.save(settings)
                return settings
            if version == 8:
                settings = migrate_v8(raw)
                self._backup_version(8)
                self.save(settings)
                return settings
            if version == 9:
                settings = migrate_v9(raw)
                self._backup_version(9)
                self.save(settings)
                return settings
            if version == 10:
                settings = migrate_v10(raw)
                self._backup_version(10)
                self.save(settings)
                return settings
            if version == 11:
                settings = migrate_v11(raw)
                self._backup_version(11)
                self.save(settings)
                return settings
            if version == 12:
                settings = migrate_v12(raw)
                self._backup_version(12)
                self.save(settings)
                return settings
            settings = settings_v13_from_dict(raw)
            voice = raw.get("voice") if isinstance(raw.get("voice"), dict) else {}
            profiles = voice.get("asr_profiles") if isinstance(voice, dict) else []
            persisted_profile_ids = {
                str(profile.get("id", ""))
                for profile in profiles
                if isinstance(profile, dict)
            } if isinstance(profiles, list) else set()
            loaded_profile_ids = {
                profile.id for profile in settings.voice.asr_profiles
            }
            persisted_ocr = (
                raw.get("ocr") if isinstance(raw.get("ocr"), dict) else {}
            )
            if (
                persisted_profile_ids != loaded_profile_ids
                or "model_package" not in persisted_ocr
                or "self_voice" not in raw
            ):
                self.save(settings)
            return settings
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
            json.dumps(settings_v13_to_dict(settings), ensure_ascii=False, indent=2),
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
