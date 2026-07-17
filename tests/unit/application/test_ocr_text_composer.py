from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.use_cases.ocr.session_cache import (
    SessionTranslationCache,
)
from vrctranslate.application.use_cases.ocr.text_composer import compose_ocr_texts
from vrctranslate.application.use_cases.ocr.translation_context import (
    RecentOcrContext,
)
from vrctranslate.domain.ocr import OcrText
from vrctranslate.domain.translation import TranslationRequest


def _text(
    value: str,
    left: int,
    top: int,
    right: int,
    bottom: int,
    confidence: float = 0.9,
) -> OcrText:
    return OcrText(
        value,
        confidence,
        ((left, top), (right, top), (right, bottom), (left, bottom)),
    )


def test_composer_restores_reading_order_and_joins_wrapped_latin_text() -> None:
    result = compose_ocr_texts(
        [
            _text("from VRChat", 10, 30, 105, 44),
            _text("world", 55, 10, 105, 24),
            _text("Hello", 10, 10, 50, 24),
        ]
    )

    assert [item.text for item in result] == ["Hello world from VRChat"]


def test_composer_joins_cjk_wraps_without_inserting_spaces() -> None:
    result = compose_ocr_texts(
        [
            _text("こん", 10, 10, 50, 24),
            _text("にちは", 10, 29, 65, 43),
        ]
    )

    assert [item.text for item in result] == ["こんにちは"]


def test_composer_does_not_mix_distant_text_on_the_same_row() -> None:
    result = compose_ocr_texts(
        [
            _text("left bubble", 10, 10, 100, 25),
            _text("right bubble", 320, 10, 420, 25),
        ]
    )

    assert [item.text for item in result] == ["left bubble", "right bubble"]


def test_composer_preserves_items_without_geometry() -> None:
    source = [OcrText("first", 0.9), OcrText("second", 0.8)]

    assert compose_ocr_texts(source) == source


def test_recent_context_is_bounded_expires_and_never_persists() -> None:
    context = RecentOcrContext(max_items=2, ttl_seconds=5, max_characters=20)

    assert context.prepare("first", now=0) == ()
    assert context.prepare("second", now=1) == ("first",)
    assert context.prepare("third", now=2) == ("first", "second")
    assert context.prepare("after expiry", now=10) == ()
    context.clear()
    assert context.prepare("new session", now=11) == ()


def test_llm_cache_separates_context_but_regular_provider_cache_does_not() -> None:
    first = TranslationRequest("1", "current", "en", "zh-CN", "ocr", ("one",))
    second = TranslationRequest("2", "current", "en", "zh-CN", "ocr", ("two",))

    llm = TranslationProfile(id="llm", provider="openai_compatible")
    deepl = TranslationProfile(id="deepl", provider="deepl")
    assert SessionTranslationCache.key(first, llm) != SessionTranslationCache.key(
        second, llm
    )
    assert SessionTranslationCache.key(first, deepl) == SessionTranslationCache.key(
        second, deepl
    )
