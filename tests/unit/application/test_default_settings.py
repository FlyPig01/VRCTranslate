from vrctranslate.application.dto import AppSettings, TranslationProfile, TranslationSettings


def test_release_defaults_are_portable_and_contain_no_private_service() -> None:
    settings = AppSettings()

    assert len(settings.translation.profiles) == 1
    profile = settings.translation.profiles[0]
    assert profile.provider == "test"
    assert profile.api_key == ""
    assert profile.base_url == ""
    assert profile.model == ""
    assert profile.region == ""
    assert profile.timeout_seconds == 8.0

    assert settings.translation.self_route.profile_id == "test"
    assert settings.translation.self_route.source_language == "auto"
    assert settings.translation.self_route.target_language == "zh-CN"
    assert settings.translation.self_route.romaji_mode == "auto"
    assert settings.translation.self_route.glossary_enabled is True
    assert settings.translation.ocr_route.profile_id == "test"
    assert settings.translation.ocr_route.source_language == "ja"
    assert settings.translation.ocr_route.target_language == "zh-CN"
    assert settings.translation.ocr_route.romaji_mode == "off"
    assert settings.translation.ocr_route.glossary_enabled is True
    assert settings.translation.voice_route.profile_id == "test"
    assert settings.translation.voice_route.source_language == "auto"
    assert settings.translation.voice_route.target_language == "zh-CN"
    assert settings.translation.voice_route.glossary_enabled is True
    assert settings.translation.voice_route.translation_strategy == "text_profile"
    assert settings.voice.target_process_name == "VRChat.exe"
    assert settings.voice.asr_profile_id == ""
    assert settings.voice.asr_profiles == []
    assert settings.voice.overlay.display_mode == "both"
    assert settings.self_voice.enabled is False
    assert settings.self_voice.microphone_id == ""
    assert settings.self_voice.source_language == "zh-CN"
    assert settings.self_voice.activation_scope == "vrchat_foreground"
    assert settings.self_voice.toggle_hotkey == "Ctrl+Alt+M"
    assert settings.glossary.enabled is True
    assert settings.glossary.builtin_enabled is True

    assert settings.ocr.window_title == "VRChat"
    assert settings.ocr.recognition_mode == "continuous"
    assert settings.ocr.region_width == 0
    assert settings.ocr.region_height == 0
    assert settings.ui.ocr_display_mode == "overlay"
    assert settings.ui.quick_input_hotkey == "Ctrl+Alt+I"


def test_multimodal_profile_cannot_become_the_self_message_route() -> None:
    settings = TranslationSettings(
        profiles=[
            TranslationProfile(
                id="vision",
                name="Vision",
                provider="multimodal_openai",
            )
        ]
    )
    settings.self_route.profile_id = "vision"
    settings.ocr_route.profile_id = "vision"

    settings.ensure_routes()

    assert settings.profile(settings.self_route.profile_id).provider == "test"
    assert settings.ocr_route.profile_id == "vision"
    assert settings.profile("vision").timeout_seconds == 8.0
