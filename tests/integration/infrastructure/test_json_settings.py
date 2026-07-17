import json

from vrctranslate.application.dto import AppSettings
from vrctranslate.infrastructure.settings.json_repository import JsonSettingsRepository


def test_config_round_trip_keeps_plain_text_api_key(tmp_path) -> None:
    path = tmp_path / "config.json"
    repository = JsonSettingsRepository(path)
    settings = AppSettings()
    settings.translation.profiles[0].api_key = "plain-test-key"
    repository.save(settings)
    raw = json.loads(path.read_text(encoding="utf-8"))
    assert raw["version"] == 2
    assert raw["translation"]["profiles"][0]["api_key"] == "plain-test-key"
    assert repository.load().translation.profiles[0].api_key == "plain-test-key"
    assert "sync_typing" not in raw["osc"]
    assert raw["ocr"]["capture_backend"] == "auto"


def test_invalid_values_are_clamped(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps({"osc": {"port": 99999}, "ocr": {"confidence": -2}}),
        encoding="utf-8",
    )
    settings = JsonSettingsRepository(path).load()
    assert settings.osc.port == 65535
    assert settings.ocr.confidence == 0


def test_broken_config_is_renamed_and_replaced_with_defaults(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text("{broken", encoding="utf-8")
    settings = JsonSettingsRepository(path).load()
    assert settings == AppSettings()
    assert path.exists()
    assert json.loads(path.read_text(encoding="utf-8"))["version"] == 2
    assert list(tmp_path.glob("config.json.broken-*"))


def test_version_one_is_migrated_to_profiles_and_independent_routes(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 1,
                "translation": {
                    "provider": "openai_compatible",
                    "base_url": "https://example.invalid/v1",
                    "api_key": "legacy-key",
                    "model": "legacy-model",
                },
                "osc": {"sync_typing": False},
                "ui": {"target_language": "ja", "always_on_top": True},
            }
        ),
        encoding="utf-8",
    )
    settings = JsonSettingsRepository(path).load()
    assert settings.version == 2
    assert settings.translation.profiles[0].api_key == "legacy-key"
    assert settings.translation.self_route.target_language == "ja"
    assert settings.translation.ocr_route.target_language == "ja"
    assert path.with_name("config.json.v1-backup").exists()
    raw = json.loads(path.read_text(encoding="utf-8"))
    assert "sync_typing" not in raw["osc"]
    assert "always_on_top" not in raw["ui"]
