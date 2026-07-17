from __future__ import annotations

import os

from vrctranslate.infrastructure.paths import discover_app_paths
from vrctranslate.infrastructure.translation.argos_model_manager import ArgosModelManager


def test_argos_environment_is_redirected_before_lazy_import(monkeypatch, tmp_path) -> None:
    data = tmp_path / "portable-data"
    monkeypatch.setenv("VRC_TRANSLATE_DATA_DIR", str(data))
    paths = discover_app_paths()
    manager = ArgosModelManager(paths)
    assert manager.model_directory == str(data / "models" / "argos")
    assert os.environ["ARGOS_PACKAGES_DIR"] == str(data / "models" / "argos")
    assert os.environ["XDG_CACHE_HOME"] == str(data / "cache" / "third_party")
    assert paths.config_file == data / "config.json"
    assert paths.data_root.exists()
