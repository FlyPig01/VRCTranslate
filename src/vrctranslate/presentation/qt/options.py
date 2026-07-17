from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from vrctranslate.presentation.qt.i18n import I18nManager


def languages(i18n: I18nManager) -> list[tuple[str, str]]:
    codes = [
        "auto",
        "zh-CN",
        "zh-TW",
        "en",
        "ja",
        "ko",
        "fr",
        "de",
        "es",
        "ru",
    ]
    return [(i18n.tr(f"lang.{code.replace('-', '_')}"), code) for code in codes]


def formats(i18n: I18nManager) -> list[tuple[str, str]]:
    codes = [
        "translation_only",
        "original_then_translation",
        "translation_then_original",
    ]
    return [(i18n.tr(f"format.{code}"), code) for code in codes]
