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


def test_composer_keeps_source_line_boxes_for_inline_rendering() -> None:
    first = ((10, 10), (90, 10), (90, 28), (10, 28))
    second = ((10, 32), (110, 32), (110, 50), (10, 50))
    source = [
        OcrText("第一行", 0.9, first, (first,), (300, 150), 0.2),
        OcrText("第二行", 0.8, second, (second,), (300, 150), 0.4),
    ]

    result = compose_ocr_texts(source)

    assert len(result) == 1
    assert result[0].line_boxes == (first, second)
    assert result[0].canvas_size == (300, 150)
    assert 0.2 < result[0].background_luminance < 0.4


def test_composer_separates_heading_paragraph_and_list_items_for_inline_layout() -> None:
    result = compose_ocr_texts(
        [
            _text("12.10 UI 与配置", 20, 10, 220, 42),
            _text(
                "OCR 页面在译文显示区域增加显示方式，推荐提供：",
                20,
                58,
                520,
                82,
            ),
            _text("• 独立译文浮窗。", 45, 98, 250, 122),
            _text("• 识别区域嵌字。", 45, 136, 250, 160),
            _text("• 两者同时显示。", 45, 174, 250, 198),
        ]
    )

    assert [item.text for item in result] == [
        "12.10 UI 与配置",
        "OCR 页面在译文显示区域增加显示方式，推荐提供：",
        "• 独立译文浮窗。",
        "• 识别区域嵌字。",
        "• 两者同时显示。",
    ]


def test_composer_keeps_wrapped_lines_in_the_same_paragraph() -> None:
    result = compose_ocr_texts(
        [
            _text("This is a paragraph that continues", 20, 20, 420, 44),
            _text("onto a second wrapped line.", 20, 50, 330, 74),
        ]
    )

    assert [item.text for item in result] == [
        "This is a paragraph that continues onto a second wrapped line."
    ]


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
