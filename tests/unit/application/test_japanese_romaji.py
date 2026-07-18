from __future__ import annotations

import pytest

from vrctranslate.application.text_preprocessing.japanese_romaji import (
    preprocess_romaji,
)
from vrctranslate.infrastructure.text.wanakana_converter import (
    WanaKanaRomajiConverter,
)


CONVERTER = WanaKanaRomajiConverter()


@pytest.mark.parametrize(
    ("source", "expected"),
    (
        ("konnichiwa", "こんにちは"),
        ("matcha", "まっちゃ"),
        ("zasshi", "ざっし"),
        ("shin'you", "しんよう"),
        ("toukyou", "とうきょう"),
        ("watashiwagakuseidesu", "わたしわがくせいです"),
        ("yoroshikuonegaishimasu", "よろしくおねがいします"),
        (
            "kyouwaisshoniasondearigatou",
            "きょうわいっしょにあそんでありがとう",
        ),
    ),
)
def test_auto_mode_converts_high_confidence_continuous_romaji(
    source: str,
    expected: str,
) -> None:
    result = preprocess_romaji(source, "ja", "auto", CONVERTER)

    assert result.text == expected
    assert result.changed
    assert not result.unparsed_segments


@pytest.mark.parametrize(
    ("source", "expected"),
    (
        pytest.param(
            "Konnichiwa, genki desu ka?",
            "こんにちは、げんきですか？",
            id="vrchat-romaji-01",
        ),
        pytest.param(
            "Kyou no tenki wa hare desu.",
            "きょうのてんきははれです。",
            id="vrchat-romaji-02",
        ),
        pytest.param(
            "Koohii o nihai kudasai.",
            "コーヒーを二杯ください。",
            id="vrchat-romaji-03",
        ),
        pytest.param(
            "Gohan o tabemashita ka?",
            "ごはんをたべましたか？",
            id="vrchat-romaji-04",
        ),
        pytest.param(
            "Watashi wa ashita, Toukyou ni ikimasu.",
            "わたしはあした、とうきょうにいきます。",
            id="vrchat-romaji-05",
        ),
        pytest.param(
            "Kono waarudo wa totemo hiroi desu ne.",
            "このワールドはとてもひろいですね。",
            id="vrchat-romaji-06",
        ),
        pytest.param(
            "Onryou o chiisaku shite kuremasen ka?",
            "音量をちいさくしてくれませんか？",
            id="vrchat-romaji-07",
        ),
        pytest.param(
            "Abataa o henkou suru houhou o oshiete kudasai.",
            "アバターをへんこうするほうほうをおしえてください。",
            id="vrchat-romaji-08",
        ),
        pytest.param(
            "Kinou no ibento wa tanoshikatta desu.",
            "きのうのイベントはたのしかったです。",
            id="vrchat-romaji-09",
        ),
        pytest.param(
            "Tsugi wa doko ni ikimasu ka?",
            "つぎはどこにいきますか？",
            id="vrchat-romaji-10",
        ),
    ),
)
def test_auto_mode_handles_vrchat_romaji_sentence_examples(
    source: str,
    expected: str,
) -> None:
    result = preprocess_romaji(source, "ja", "auto", CONVERTER)

    assert result.text == expected
    assert result.changed
    assert not result.unparsed_segments


@pytest.mark.parametrize(
    "source",
    (
        "hello",
        "thank you",
        "game start",
        "VRChat",
        "YouTube",
        "https://example.com",
        "Player_123",
        "LOL",
    ),
)
def test_auto_mode_preserves_english_urls_usernames_and_brands(source: str) -> None:
    result = preprocess_romaji(source, "ja", "auto", CONVERTER)

    assert result.text == source
    assert not result.changed


@pytest.mark.parametrize(
    ("source", "expected"),
    (
        ("konnichiwa VRChat", "こんにちは VRChat"),
        ("nani kore lol", "なにこれ lol"),
        ("@Player konnichiwa", "@Player こんにちは"),
        ("VRChat de asobou", "VRChat であそぼう"),
    ),
)
def test_auto_mode_converts_romaji_while_preserving_mixed_tokens(
    source: str,
    expected: str,
) -> None:
    assert preprocess_romaji(source, "ja", "auto", CONVERTER).text == expected


def test_off_mode_never_converts() -> None:
    result = preprocess_romaji("konnichiwa", "ja", "off", CONVERTER)

    assert result.text == "konnichiwa"
    assert not result.changed


def test_force_mode_converts_ambiguous_but_complete_romaji() -> None:
    result = preprocess_romaji("chekku fa fi", "ja", "force", CONVERTER)

    assert result.text == "ちぇっくふぁふぃ"


def test_incomplete_conversion_never_emits_half_kana_in_auto_mode() -> None:
    result = preprocess_romaji("party", "ja", "auto", CONVERTER)

    assert result.text == "party"


def test_non_japanese_route_never_converts() -> None:
    result = preprocess_romaji("konnichiwa", "en", "force", CONVERTER)

    assert result.text == "konnichiwa"
    assert not result.changed
