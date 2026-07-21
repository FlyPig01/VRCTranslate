from __future__ import annotations

from typing import TYPE_CHECKING

from vrctranslate.domain.languages import (
    OCR_SOURCE_LANGUAGE_CODES,
    TRANSLATION_LANGUAGE_CODES,
    normalize_language_code,
)

if TYPE_CHECKING:
    from vrctranslate.application.dto import SpeechRecognitionProfile


_TRANSLATION_AUTO_DETECT = frozenset(
    {
        "test",
        "deepl",
        "google_cloud",
        "google_free",
        "aliyun",
        "openai_compatible",
        "multimodal_openai",
    }
)


def translation_language_codes(
    provider: str,
    *,
    source: bool,
    ocr: bool = False,
) -> tuple[str, ...]:
    languages = OCR_SOURCE_LANGUAGE_CODES if ocr and source else TRANSLATION_LANGUAGE_CODES
    if source and provider in _TRANSLATION_AUTO_DETECT and not ocr:
        return ("auto", *languages)
    return languages


def translation_supports_auto_detect(provider: str) -> bool:
    return provider in _TRANSLATION_AUTO_DETECT


_TENCENT_ENGINE_LANGUAGES: dict[str, tuple[str, ...]] = {
    "16k_zh": ("zh-CN",),
    "16k_zh_en": ("zh-CN", "en"),
    "16k_zh-TW": ("zh-TW",),
    "16k_yue": ("zh-CN",),
    "16k_en": ("en",),
    "16k_en_large": ("en",),
    "16k_en_game": ("en",),
    "16k_en_edu": ("en",),
    "16k_ja": ("ja",),
    "16k_ko": ("ko",),
    "16k_es": ("es",),
    "16k_fr": ("fr",),
    "16k_de": ("de",),
    "16k_multi_lang": ("en", "ja", "ko", "es", "fr", "de"),
}


def speech_source_language_codes(
    profile: SpeechRecognitionProfile | None,
) -> tuple[str, ...]:
    if profile is None:
        return ("auto", *TRANSLATION_LANGUAGE_CODES)
    if profile.provider == "local_offline":
        # SenseVoiceSmall is retained for the existing CJK/English path. Korean is
        # the only newly exposed language in this multilingual expansion.
        return ("auto", "zh-CN", "en", "ja", "ko")
    if profile.provider == "tencent_realtime":
        model = profile.model.strip()
        if model in _TENCENT_ENGINE_LANGUAGES:
            return _TENCENT_ENGINE_LANGUAGES[model]
        if model.startswith("16k_zh"):
            return ("zh-CN",)
        return TRANSLATION_LANGUAGE_CODES
    if profile.provider == "aliyun_nls_realtime":
        selected = normalize_language_code(
            str(profile.options.get("language", "auto") or "auto")
        )
        if selected in TRANSLATION_LANGUAGE_CODES:
            return (selected,)
        return ("auto", *TRANSLATION_LANGUAGE_CODES)
    return ("auto", *TRANSLATION_LANGUAGE_CODES)
