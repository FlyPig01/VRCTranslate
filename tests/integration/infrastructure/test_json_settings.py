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
    assert raw["version"] == 4
    assert raw["translation"]["profiles"][0]["api_key"] == "plain-test-key"
    assert repository.load().translation.profiles[0].api_key == "plain-test-key"
    assert "sync_typing" not in raw["osc"]
    assert raw["ocr"]["capture_backend"] == "auto"
    assert raw["ocr"]["recognition_mode"] == "continuous"
    assert "duplicate_seconds" not in raw["ocr"]
    assert raw["ui"]["ocr_orb_topmost"] is True
    assert raw["ui"]["ocr_overlay_show_original"] is True
    assert raw["ui"]["ocr_display_mode"] == "overlay"


def test_invalid_values_are_clamped(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps({"osc": {"port": 99999}, "ocr": {"confidence": -2}}),
        encoding="utf-8",
    )
    settings = JsonSettingsRepository(path).load()
    assert settings.osc.port == 65535
    assert settings.ocr.confidence == 0


def test_removed_or_unknown_provider_falls_back_to_default_profile(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 2,
                "translation": {
                    "profiles": [
                        {
                            "id": "removed-provider",
                            "name": "Unavailable local provider",
                            "provider": "unsupported_local_provider",
                        }
                    ],
                    "self_route": {"profile_id": "removed-provider"},
                    "ocr_route": {"profile_id": "removed-provider"},
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()

    assert [profile.provider for profile in settings.translation.profiles] == [
        "test"
    ]
    assert settings.translation.self_route.profile_id == "test"
    assert settings.translation.ocr_route.profile_id == "test"


def test_broken_config_is_renamed_and_replaced_with_defaults(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text("{broken", encoding="utf-8")
    settings = JsonSettingsRepository(path).load()
    assert settings == AppSettings()
    assert path.exists()
    assert json.loads(path.read_text(encoding="utf-8"))["version"] == 4
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
    assert settings.version == 4
    assert settings.translation.profiles[0].api_key == "legacy-key"
    assert settings.translation.self_route.target_language == "ja"
    assert settings.translation.ocr_route.target_language == "ja"
    assert path.with_name("config.json.v1-backup").exists()
    raw = json.loads(path.read_text(encoding="utf-8"))
    assert "sync_typing" not in raw["osc"]
    assert "always_on_top" not in raw["ui"]


def test_version_two_migrates_to_continuous_mode_without_duplicate_cooldown(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 2,
                "ocr": {
                    "capture_backend": "auto",
                    "change_threshold": 6,
                    "duplicate_seconds": 9,
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw = json.loads(path.read_text(encoding="utf-8"))

    assert settings.version == 4
    assert settings.ocr.recognition_mode == "continuous"
    assert settings.ocr.change_threshold == 6
    assert "duplicate_seconds" not in raw["ocr"]
    assert path.with_name("config.json.v2-backup").exists()


def test_version_three_adds_inline_defaults_and_explicit_ocr_language(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 3,
                "translation": {"ocr_route": {"source_language": "auto"}},
                "ui": {"ocr_overlay_opacity": 0.7},
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw = json.loads(path.read_text(encoding="utf-8"))

    assert settings.version == 4
    assert settings.translation.ocr_route.source_language == "ja"
    assert settings.ui.ocr_display_mode == "overlay"
    assert settings.ui.ocr_inline_opacity == 0.9
    assert raw["version"] == 4
    assert path.with_name("config.json.v3-backup").exists()
