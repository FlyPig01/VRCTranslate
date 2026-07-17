from __future__ import annotations

import json
from pathlib import Path
from string import Formatter


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
    assert set(documents) == {"zh_CN", "en_US", "ja_JP"}
    for locale, values in documents.items():
        assert set(values) == expected, locale


def test_translation_placeholders_match_in_every_locale() -> None:
    documents = _documents()
    for key, reference in documents["zh_CN"].items():
        expected = _fields(reference)
        for locale, values in documents.items():
            assert _fields(values[key]) == expected, (locale, key)
