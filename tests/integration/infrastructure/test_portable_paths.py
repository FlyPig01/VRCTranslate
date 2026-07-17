from __future__ import annotations

from vrctranslate.infrastructure.paths import discover_app_paths


def test_writable_paths_are_redirected_to_portable_data(monkeypatch, tmp_path) -> None:
    data = tmp_path / "portable-data"
    monkeypatch.setenv("VRC_TRANSLATE_DATA_DIR", str(data))
    paths = discover_app_paths()
    paths.ensure_writable()

    assert paths.config_file == data / "config.json"
    assert paths.logs_dir == data / "logs"
    assert paths.cache_dir == data / "cache"
    assert paths.data_root.is_dir()
    assert paths.logs_dir.is_dir()
    assert paths.cache_dir.is_dir()
