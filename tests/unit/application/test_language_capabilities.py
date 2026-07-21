from __future__ import annotations

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.application.language_capabilities import (
    speech_source_language_codes,
    translation_language_codes,
)
from vrctranslate.domain.languages import (
    INTERFACE_LOCALES,
    OCR_MODEL_PACKAGE_IDS,
    ocr_package_for_language,
)


def test_interface_locales_use_stable_native_names() -> None:
    assert [(item.locale, item.native_name) for item in INTERFACE_LOCALES] == [
        ("zh_CN", "简体中文"),
        ("zh_TW", "繁體中文"),
        ("en_US", "English"),
        ("ja_JP", "日本語"),
        ("ko_KR", "한국어"),
        ("fr_FR", "Français"),
        ("de_DE", "Deutsch"),
        ("es_ES", "Español"),
        ("ru_RU", "Русский"),
    ]


def test_natural_languages_map_to_shared_ocr_packages() -> None:
    assert OCR_MODEL_PACKAGE_IDS == (
        "zh-CN",
        "ja",
        "en",
        "ko",
        "latin",
        "cyrillic",
    )
    assert ocr_package_for_language("zh-TW") == "zh-CN"
    assert ocr_package_for_language("ko") == "ko"
    assert ocr_package_for_language("fr") == "latin"
    assert ocr_package_for_language("de") == "latin"
    assert ocr_package_for_language("es") == "latin"
    assert ocr_package_for_language("ru") == "cyrillic"


def test_translation_source_options_follow_provider_capabilities() -> None:
    assert translation_language_codes("tencent", source=True)[0] == "zh-CN"
    assert "auto" not in translation_language_codes("tencent", source=True)
    assert translation_language_codes("deepl", source=True)[0] == "auto"
    assert "auto" not in translation_language_codes("deepl", source=False)


def test_speech_source_options_follow_selected_profile() -> None:
    local = SpeechRecognitionProfile(
        provider="local_offline",
        model="sensevoice-small-int8",
    )
    assert speech_source_language_codes(local) == (
        "auto",
        "zh-CN",
        "en",
        "ja",
        "ko",
    )
    japanese = SpeechRecognitionProfile(
        provider="tencent_realtime",
        model="16k_ja",
    )
    assert speech_source_language_codes(japanese) == ("ja",)

    multilingual = SpeechRecognitionProfile(
        provider="tencent_realtime",
        model="16k_multi_lang",
    )
    assert speech_source_language_codes(multilingual) == (
        "en",
        "ja",
        "ko",
        "es",
        "fr",
        "de",
    )
