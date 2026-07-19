import json

from vrctranslate.application.dto import (
    AppSettings,
    SpeechRecognitionProfile,
    TranslationProfile,
)
from vrctranslate.infrastructure.settings.json_repository import JsonSettingsRepository


def test_config_round_trip_keeps_plain_text_api_key(tmp_path) -> None:
    path = tmp_path / "config.json"
    repository = JsonSettingsRepository(path)
    settings = AppSettings()
    settings.translation.profiles[0].api_key = "plain-test-key"
    settings.translation.ocr_route.source_language = "en"
    repository.save(settings)
    raw = json.loads(path.read_text(encoding="utf-8"))
    assert raw["version"] == 12
    assert raw["translation"]["profiles"][0]["api_key"] == "plain-test-key"
    assert repository.load().translation.profiles[0].api_key == "plain-test-key"
    assert "sync_typing" not in raw["osc"]
    assert raw["ocr"]["capture_backend"] == "auto"
    assert raw["ocr"]["region_coordinate_space"] == "window"
    assert raw["ocr"]["recognition_mode"] == "continuous"
    assert "duplicate_seconds" not in raw["ocr"]
    assert raw["ui"]["ocr_orb_topmost"] is True
    assert raw["ui"]["ocr_overlay_show_original"] is True
    assert raw["ui"]["ocr_display_mode"] == "overlay"
    assert raw["translation"]["self_route"]["romaji_mode"] == "auto"
    assert raw["translation"]["ocr_route"]["romaji_mode"] == "off"
    assert repository.load().translation.ocr_route.source_language == "en"
    assert "romaji_to_kana" not in raw["translation"]["self_route"]
    assert raw["glossary"] == {"enabled": True, "builtin_enabled": True}
    assert raw["translation"]["self_route"]["glossary_enabled"] is True
    assert raw["translation"]["ocr_route"]["glossary_enabled"] is True
    assert raw["voice"]["asr_profile_id"] == ""
    assert raw["voice"]["asr_profiles"] == []
    assert raw["voice"]["overlay"]["display_mode"] == "both"
    assert raw["ocr"]["multimodal_interval_ms"] == 3000
    assert "ocr_overlay_display_seconds" not in raw["ui"]


def test_v12_round_trip_keeps_provider_specific_speech_fields(tmp_path) -> None:
    path = tmp_path / "config.json"
    repository = JsonSettingsRepository(path)
    settings = AppSettings()
    settings.voice.asr_profiles = [
        SpeechRecognitionProfile(
            id="tencent-asr",
            name="Tencent ASR",
            provider="tencent_realtime",
            api_key="synthetic-secret-key",
            model="16k_ja",
            options={
                "app_id": "123456",
                "secret_id": "synthetic-secret-id",
                "validation_state": "pending",
            },
        )
    ]
    settings.voice.asr_profile_id = "tencent-asr"
    settings.voice.overlay.display_mode = "original"

    repository.save(settings)
    loaded = repository.load().voice.asr_profile()

    assert loaded.provider == "tencent_realtime"
    assert loaded.api_key == "synthetic-secret-key"
    assert loaded.model == "16k_ja"
    assert loaded.options["app_id"] == "123456"
    assert loaded.options["secret_id"] == "synthetic-secret-id"
    assert repository.load().voice.overlay.display_mode == "original"


def test_v12_round_trip_keeps_local_sensevoice_profile(tmp_path) -> None:
    repository = JsonSettingsRepository(tmp_path / "config.json")
    settings = AppSettings()
    settings.voice.asr_profiles = [
        SpeechRecognitionProfile(
            id="local-asr",
            name="SenseVoice",
            provider="local_offline",
            model="sensevoice-small-int8",
            options={"validation_state": "pending", "service_vendor": "local"},
        )
    ]
    settings.voice.asr_profile_id = "local-asr"

    repository.save(settings)
    loaded = repository.load().voice.asr_profile()

    assert loaded.provider == "local_offline"
    assert loaded.model == "sensevoice-small-int8"
    assert loaded.api_key == ""
    assert loaded.options["service_vendor"] == "local"


def test_v12_round_trip_keeps_ocr_region_coordinate_space(tmp_path) -> None:
    repository = JsonSettingsRepository(tmp_path / "config.json")
    settings = AppSettings()
    settings.ocr.capture_backend = "screen"
    settings.ocr.region_coordinate_space = "screen"

    repository.save(settings)

    assert repository.load().ocr.region_coordinate_space == "screen"


def test_legacy_voice_show_original_maps_to_new_display_mode(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 12,
                "voice": {"overlay": {"show_original": False}},
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()

    assert settings.voice.overlay.display_mode == "translation"
    assert settings.voice.overlay.show_original is False


def test_tencent_config_round_trip_uses_explicit_credential_names(tmp_path) -> None:
    path = tmp_path / "config.json"
    repository = JsonSettingsRepository(path)
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(
            id="tencent-test",
            name="Tencent test",
            provider="tencent",
            api_key="test-secret-id",
            model="test-secret-key",
        )
    ]

    repository.save(settings)

    raw_profile = json.loads(path.read_text(encoding="utf-8"))["translation"][
        "profiles"
    ][0]
    assert raw_profile["secret_id"] == "test-secret-id"
    assert raw_profile["secret_key"] == "test-secret-key"
    assert "api_key" not in raw_profile
    assert "model" not in raw_profile
    loaded = repository.load().translation.profiles[0]
    assert loaded.api_key == "test-secret-id"
    assert loaded.model == "test-secret-key"


def test_aliyun_translation_profile_round_trip_keeps_region_and_api_mode(
    tmp_path,
) -> None:
    repository = JsonSettingsRepository(tmp_path / "config.json")
    settings = AppSettings()
    settings.translation.profiles.append(
        TranslationProfile(
            id="aliyun-translation",
            name="Alibaba Cloud MT",
            provider="aliyun",
            base_url="mt.ap-southeast-1.aliyuncs.com",
            api_key="test-access-key-id",
            model="test-access-key-secret",
            region="ap-southeast-1",
            options={"aliyun_api": "professional"},
        )
    )

    repository.save(settings)
    raw_profile = json.loads(
        (tmp_path / "config.json").read_text(encoding="utf-8")
    )["translation"]["profiles"][-1]
    loaded = repository.load().translation.profile("aliyun-translation")

    assert raw_profile["access_key_id"] == "test-access-key-id"
    assert raw_profile["access_key_secret"] == "test-access-key-secret"
    assert "api_key" not in raw_profile
    assert "model" not in raw_profile
    assert loaded.provider == "aliyun"
    assert loaded.region == "ap-southeast-1"
    assert loaded.options["aliyun_api"] == "professional"


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
    assert json.loads(path.read_text(encoding="utf-8"))["version"] == 12
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
    assert settings.version == 12
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

    assert settings.version == 12
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

    assert settings.version == 12
    assert settings.translation.ocr_route.source_language == "ja"
    assert settings.ui.ocr_display_mode == "overlay"
    assert settings.ui.ocr_inline_opacity == 0.9
    assert raw["version"] == 12
    assert path.with_name("config.json.v3-backup").exists()


def test_version_four_migrates_legacy_romaji_booleans_to_modes(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 4,
                "translation": {
                    "self_route": {"romaji_to_kana": False},
                    "ocr_route": {"source_language": "ja", "romaji_to_kana": True},
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw = json.loads(path.read_text(encoding="utf-8"))

    assert settings.version == 12
    assert settings.translation.self_route.romaji_mode == "off"
    assert settings.translation.ocr_route.romaji_mode == "auto"
    assert raw["translation"]["self_route"]["romaji_mode"] == "off"
    assert raw["translation"]["ocr_route"]["romaji_mode"] == "auto"
    assert "romaji_to_kana" not in raw["translation"]["ocr_route"]
    assert path.with_name("config.json.v4-backup").exists()


def test_version_five_renames_tencent_credential_fields(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 5,
                "translation": {
                    "profiles": [
                        {
                            "id": "tencent-test",
                            "name": "Tencent test",
                            "provider": "tencent",
                            "api_key": "legacy-secret-id",
                            "model": "legacy-secret-key",
                        }
                    ],
                    "self_route": {"profile_id": "tencent-test"},
                    "ocr_route": {
                        "profile_id": "tencent-test",
                        "source_language": "ja",
                    },
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw_profile = json.loads(path.read_text(encoding="utf-8"))["translation"][
        "profiles"
    ][0]

    assert settings.version == 12
    assert settings.translation.profiles[0].api_key == "legacy-secret-id"
    assert settings.translation.profiles[0].model == "legacy-secret-key"
    assert raw_profile["secret_id"] == "legacy-secret-id"
    assert raw_profile["secret_key"] == "legacy-secret-key"
    assert "api_key" not in raw_profile
    assert "model" not in raw_profile
    assert path.with_name("config.json.v5-backup").exists()


def test_version_six_enables_local_glossary_without_remote_resources(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 6,
                "translation": {
                    "self_route": {"glossary_enabled": False},
                    "ocr_route": {"source_language": "ja"},
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw = json.loads(path.read_text(encoding="utf-8"))

    assert settings.version == 12
    assert settings.glossary.enabled is True
    assert settings.glossary.builtin_enabled is True
    assert settings.translation.self_route.glossary_enabled is False
    assert settings.translation.ocr_route.glossary_enabled is True
    assert raw["version"] == 12
    assert "remote_resources" not in raw["glossary"]
    assert "remote_id" not in raw["glossary"]
    assert "sync_mode" not in raw["glossary"]
    assert path.with_name("config.json.v6-backup").exists()


def test_version_seven_removes_overlay_expiry_and_adds_multimodal_defaults(
    tmp_path,
) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 7,
                "ocr": {"interval_ms": 1200},
                "ui": {"ocr_overlay_display_seconds": 45},
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw = json.loads(path.read_text(encoding="utf-8"))

    assert settings.version == 12
    assert settings.ocr.interval_ms == 1200
    assert settings.ocr.multimodal_interval_ms == 3000
    assert raw["version"] == 12
    assert "ocr_overlay_display_seconds" not in raw["ui"]
    assert path.with_name("config.json.v7-backup").exists()


def test_version_eight_adds_pc_process_voice_defaults(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps({"version": 8, "translation": {"profiles": []}}),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw = json.loads(path.read_text(encoding="utf-8"))

    assert settings.version == 12
    assert settings.translation.voice_route.source_language == "auto"
    assert settings.voice.target_process_name == "VRChat.exe"
    assert settings.voice.asr_profile_id == ""
    assert settings.voice.asr_profiles == []
    assert raw["version"] == 12
    assert raw["voice"]["asr_profiles"] == []
    assert path.with_name("config.json.v8-backup").exists()


def test_version_nine_removes_obsolete_speech_protocols(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 9,
                "voice": {
                    "asr_profile_id": "paraformer",
                    "asr_profiles": [
                        {
                            "id": "paraformer",
                            "name": "Paraformer",
                            "provider": "openai_compatible",
                            "base_url": "https://unused.example/v1",
                            "api_key": "test-key",
                            "model": "paraformer-realtime-8k-v2",
                            "timeout_seconds": 8,
                        },
                        {
                            "id": "whisper",
                            "name": "Whisper",
                            "provider": "openai_compatible",
                            "base_url": "https://speech.example/v1",
                            "api_key": "test-key",
                            "model": "whisper-1",
                            "timeout_seconds": 8,
                        },
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    raw = json.loads(path.read_text(encoding="utf-8"))

    assert settings.version == 12
    assert settings.voice.asr_profiles == []
    assert settings.voice.asr_profile_id == ""
    assert raw["version"] == 12
    assert path.with_name("config.json.v9-backup").exists()


def test_version_ten_deletes_legacy_voice_profiles(
    tmp_path,
) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 10,
                "translation": {
                    "voice_route": {"translation_strategy": "native_voice"}
                },
                "voice": {
                    "asr_profile_id": "realtime",
                    "asr_profiles": [
                        {
                            "id": "realtime",
                            "name": "Paraformer",
                            "provider": "dashscope_realtime",
                            "api_key": "test-key",
                            "model": "paraformer-realtime-v2",
                        },
                        {
                            "id": "legacy",
                            "name": "Legacy transcription",
                            "provider": "openai_transcription",
                            "base_url": "https://speech.example/v1",
                            "api_key": "test-key",
                            "model": "whisper-1",
                        },
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()

    assert settings.version == 12
    assert settings.voice.asr_profiles == []
    assert settings.voice.asr_profile_id == ""
    assert settings.translation.voice_route.translation_strategy == "text_profile"
    assert path.with_name("config.json.v10-backup").exists()


def test_version_eleven_deletes_old_speech_credentials(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 11,
                "translation": {
                    "voice_route": {"translation_strategy": "native_voice"}
                },
                "voice": {
                    "asr_profile_id": "old",
                    "asr_profiles": [
                        {
                            "id": "old",
                            "name": "Old Paraformer",
                            "provider": "dashscope_realtime",
                            "api_key": "preserve-this-secret",
                            "model": "paraformer-realtime-v2",
                            "options": {"validation_state": "verified"},
                        }
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()

    assert settings.version == 12
    assert settings.voice.asr_profiles == []
    assert settings.voice.asr_profile_id == ""
    assert settings.translation.voice_route.translation_strategy == "text_profile"
    assert path.with_name("config.json.v11-backup").exists()


def test_current_v12_file_persists_legacy_speech_cleanup(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 12,
                "voice": {
                    "asr_profile_id": "old",
                    "asr_profiles": [
                        {
                            "id": "old",
                            "name": "Old audio model",
                            "provider": "audio_chat_completions",
                            "api_key": "delete-this-secret",
                            "model": "old-model",
                        }
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    persisted = json.loads(path.read_text(encoding="utf-8"))

    assert settings.voice.asr_profiles == []
    assert settings.voice.asr_profile_id == ""
    assert persisted["voice"]["asr_profiles"] == []
    assert "delete-this-secret" not in path.read_text(encoding="utf-8")


def test_current_v12_removes_obsolete_volcengine_profile(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "version": 12,
                "voice": {
                    "asr_profile_id": "speech-default",
                    "asr_profiles": [
                        {
                            "id": "speech-default",
                            "name": "未配置实时语音",
                            "provider": "volcengine_realtime",
                            "base_url": "",
                            "api_key": "",
                            "model": "volc.seedasr.sauc.duration",
                            "timeout_seconds": 8,
                            "options": {"validation_state": "pending"},
                        }
                    ],
                },
            }
        ),
        encoding="utf-8",
    )

    settings = JsonSettingsRepository(path).load()
    persisted = json.loads(path.read_text(encoding="utf-8"))

    assert settings.voice.asr_profile_id == ""
    assert settings.voice.asr_profiles == []
    assert persisted["voice"]["asr_profile_id"] == ""
    assert persisted["voice"]["asr_profiles"] == []
