from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True, slots=True)
class AppPaths:
    application_root: Path
    data_root: Path
    config_file: Path
    logs_dir: Path
    cache_dir: Path

    def ensure_writable(self) -> None:
        directories = (
            self.data_root,
            self.logs_dir,
            self.cache_dir,
        )
        for directory in directories:
            directory.mkdir(parents=True, exist_ok=True)
        probe = self.data_root / ".write-test"
        try:
            probe.write_text("ok", encoding="utf-8")
        finally:
            probe.unlink(missing_ok=True)


def discover_app_paths() -> AppPaths:
    explicit_data = os.environ.get("VRC_TRANSLATE_DATA_DIR", "").strip()
    legacy_home = os.environ.get("VRC_TRANSLATE_HOME", "").strip()
    if explicit_data:
        data_root = Path(explicit_data).expanduser().resolve()
        application_root = data_root.parent
    elif legacy_home:
        # Compatibility for existing tests and development commands. The old
        # variable points directly at the writable directory.
        data_root = Path(legacy_home).expanduser().resolve()
        application_root = data_root.parent
    elif getattr(sys, "frozen", False):
        application_root = Path(sys.executable).resolve().parent
        data_root = application_root / "data"
    else:
        application_root = Path(__file__).resolve().parents[3]
        data_root = application_root / "data"
    return AppPaths(
        application_root=application_root,
        data_root=data_root,
        config_file=data_root / "config.json",
        logs_dir=data_root / "logs",
        cache_dir=data_root / "cache",
    )
