from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class LanguageSpec:
    code: str
    native_name: str
    script: str
    ocr_package: str | None


@dataclass(frozen=True, slots=True)
class InterfaceLocale:
    locale: str
    native_name: str


LANGUAGES: tuple[LanguageSpec, ...] = (
    LanguageSpec("zh-CN", "简体中文", "han", "zh-CN"),
    LanguageSpec("zh-TW", "繁體中文", "han", "zh-CN"),
    LanguageSpec("en", "English", "latin", "en"),
    LanguageSpec("ja", "日本語", "japanese", "ja"),
    LanguageSpec("ko", "한국어", "korean", "ko"),
    LanguageSpec("fr", "Français", "latin", "latin"),
    LanguageSpec("de", "Deutsch", "latin", "latin"),
    LanguageSpec("es", "Español", "latin", "latin"),
    LanguageSpec("ru", "Русский", "cyrillic", "cyrillic"),
)

LANGUAGE_BY_CODE = {item.code: item for item in LANGUAGES}
TRANSLATION_LANGUAGE_CODES = tuple(item.code for item in LANGUAGES)
OCR_SOURCE_LANGUAGE_CODES = tuple(
    item.code for item in LANGUAGES if item.ocr_package is not None
)
OCR_MODEL_PACKAGE_IDS = ("zh-CN", "ja", "en", "ko", "latin", "cyrillic")

INTERFACE_LOCALES: tuple[InterfaceLocale, ...] = (
    InterfaceLocale("zh_CN", "简体中文"),
    InterfaceLocale("zh_TW", "繁體中文"),
    InterfaceLocale("en_US", "English"),
    InterfaceLocale("ja_JP", "日本語"),
    InterfaceLocale("ko_KR", "한국어"),
    InterfaceLocale("fr_FR", "Français"),
    InterfaceLocale("de_DE", "Deutsch"),
    InterfaceLocale("es_ES", "Español"),
    InterfaceLocale("ru_RU", "Русский"),
)


def normalize_language_code(value: str) -> str:
    aliases = {
        "zh": "zh-CN",
        "zh_CN": "zh-CN",
        "zh-Hans": "zh-CN",
        "zh_TW": "zh-TW",
        "zh-Hant": "zh-TW",
        "en-US": "en",
        "en_US": "en",
        "ja-JP": "ja",
        "ja_JP": "ja",
        "ko-KR": "ko",
        "ko_KR": "ko",
    }
    return aliases.get(value, value)


def ocr_package_for_language(value: str) -> str:
    code = normalize_language_code(value)
    if code == "auto":
        return "ja"
    if code in OCR_MODEL_PACKAGE_IDS:
        return code
    spec = LANGUAGE_BY_CODE.get(code)
    if spec is None or spec.ocr_package is None:
        raise ValueError(f"unsupported OCR language: {value}")
    return spec.ocr_package
