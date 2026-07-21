from __future__ import annotations

import json
from pathlib import Path
from string import Formatter

import pytest

from vrctranslate.presentation.qt.i18n import I18nManager


LOCALES = (
    Path(__file__).parents[3]
    / "src"
    / "vrctranslate"
    / "presentation"
    / "qt"
    / "i18n"
    / "locales"
)


def _documents() -> dict[str, dict[str, str]]:
    return {
        path.stem: json.loads(path.read_text(encoding="utf-8"))
        for path in LOCALES.glob("*.json")
    }


def _fields(value: str) -> set[str]:
    return {
        field_name
        for _, field_name, _, _ in Formatter().parse(value)
        if field_name
    }


def test_all_locales_have_exactly_the_same_keys() -> None:
    documents = _documents()
    expected = set(documents["zh_CN"])
    assert set(documents) == {
        "zh_CN",
        "zh_TW",
        "en_US",
        "ja_JP",
        "ko_KR",
        "fr_FR",
        "de_DE",
        "es_ES",
        "ru_RU",
    }
    for locale, values in documents.items():
        assert set(values) == expected, locale


def test_translation_placeholders_match_in_every_locale() -> None:
    documents = _documents()
    for key, reference in documents["zh_CN"].items():
        expected = _fields(reference)
        for locale, values in documents.items():
            assert _fields(values[key]) == expected, (locale, key)


@pytest.mark.parametrize(
    "locale",
    (
        "zh_CN",
        "zh_TW",
        "en_US",
        "ja_JP",
        "ko_KR",
        "fr_FR",
        "de_DE",
        "es_ES",
        "ru_RU",
    ),
)
def test_every_interface_locale_loads(locale: str) -> None:
    manager = I18nManager(locale)
    assert manager.locale == locale
    assert manager.tr("nav.settings") != "nav.settings"


def test_unknown_interface_locale_falls_back_to_english() -> None:
    manager = I18nManager("unsupported_LOCALE")
    assert manager.locale == "en_US"
    assert manager.tr("nav.settings") == "Settings"
