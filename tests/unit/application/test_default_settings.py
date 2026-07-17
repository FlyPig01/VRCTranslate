from vrctranslate.application.dto import AppSettings


def test_release_defaults_are_portable_and_contain_no_private_service() -> None:
    settings = AppSettings()

    assert len(settings.translation.profiles) == 1
    profile = settings.translation.profiles[0]
    assert profile.provider == "test"
    assert profile.api_key == ""
    assert profile.base_url == ""
    assert profile.model == ""
    assert profile.region == ""

    assert settings.translation.self_route.profile_id == "test"
    assert settings.translation.self_route.source_language == "auto"
    assert settings.translation.self_route.target_language == "zh-CN"
    assert settings.translation.ocr_route.profile_id == "test"
    assert settings.translation.ocr_route.source_language == "ja"
    assert settings.translation.ocr_route.target_language == "zh-CN"

    assert settings.ocr.window_title == "VRChat"
    assert settings.ocr.recognition_mode == "continuous"
    assert settings.ocr.region_width == 0
    assert settings.ocr.region_height == 0
    assert settings.ui.ocr_display_mode == "overlay"
