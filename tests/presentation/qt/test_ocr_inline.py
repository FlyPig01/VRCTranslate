from __future__ import annotations

from PySide6.QtCore import QRectF, Qt

from vrctranslate.application.dto import UiSettings
from vrctranslate.domain.ocr import CaptureRegion, OcrText, WindowInfo
from vrctranslate.presentation.qt.windows.ocr_inline import OcrInlineWindow


def _source(text: str = "hello") -> OcrText:
    box = ((30, 30), (150, 30), (150, 60), (30, 60))
    return OcrText(text, 0.95, box, (box,), (300, 150), 0.2)


def _positioned_source(
    text: str,
    left: int,
    top: int,
    right: int,
    bottom: int,
) -> OcrText:
    box = ((left, top), (right, top), (right, bottom), (left, bottom))
    return OcrText(text, 0.95, box, (box,), (300, 180), 0.2)


def test_inline_window_paints_over_source_and_is_input_transparent(qtbot) -> None:
    window = OcrInlineWindow()
    qtbot.addWidget(window)
    window.apply_settings(UiSettings(ocr_display_mode="inline"))
    window.set_target(
        WindowInfo(1, "VRChat", 0, 0, 300, 150),
        CaptureRegion(0, 0, 300, 150),
    )
    window.add_translation("request", _source(), "你好，世界", 10)
    qtbot.waitUntil(window.isVisible)

    image = window.grab().toImage()
    assert image.pixelColor(50, 40).alpha() > 0
    assert image.pixelColor(5, 5).alpha() == 0
    assert window.windowFlags() & Qt.WindowType.WindowTransparentForInput
    assert window.testAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)

    window.set_target_visible(False)
    assert window.isHidden()
    window.close_permanently()


def test_new_inline_result_replaces_an_overlapping_old_result(qtbot) -> None:
    window = OcrInlineWindow()
    qtbot.addWidget(window)
    window.apply_settings(UiSettings(ocr_display_mode="inline"))
    window.set_target(
        WindowInfo(1, "VRChat", 0, 0, 300, 150),
        CaptureRegion(0, 0, 300, 150),
    )

    window.add_translation("old", _source("old"), "旧译文", 10)
    window.add_translation("new", _source("new"), "新译文", 10)

    assert list(window._entries) == ["new"]
    window.close_permanently()


def test_inline_background_does_not_cover_unused_recognition_area(qtbot) -> None:
    window = OcrInlineWindow()
    qtbot.addWidget(window)
    window.apply_settings(UiSettings(ocr_display_mode="inline"))
    window.set_target(
        WindowInfo(1, "VRChat", 0, 0, 300, 150),
        CaptureRegion(0, 0, 300, 150),
    )
    window.add_translation("request", _source(), "短译文", 10)
    qtbot.waitUntil(window.isVisible)

    image = window.grab().toImage()

    assert image.pixelColor(80, 45).alpha() > 0
    assert image.pixelColor(100, 105).alpha() == 0
    window.close_permanently()


def test_single_inline_entry_persists_without_an_expiry_time(qtbot) -> None:
    window = OcrInlineWindow()
    qtbot.addWidget(window)
    window.apply_settings(UiSettings(ocr_display_mode="inline"))
    window.set_target(
        WindowInfo(1, "VRChat", 0, 0, 300, 150),
        CaptureRegion(0, 0, 300, 150),
    )
    window.add_translation("single", _source(), "持续显示", None)

    window._remove_expired()

    assert window._entries["single"].expires_at is None
    assert window.isVisible()
    window.close_permanently()


def test_inline_layout_uses_column_width_without_crossing_next_source_block(qtbot) -> None:
    window = OcrInlineWindow()
    qtbot.addWidget(window)
    window.resize(300, 180)
    window.add_translation(
        "left",
        _positioned_source("left", 20, 20, 130, 48),
        "左侧的较长译文",
        None,
    )
    window.add_translation(
        "right",
        _positioned_source("right", 190, 20, 280, 48),
        "右侧译文",
        None,
    )

    prepared = window._prepare_entries()
    left_bounds = window._layout_bounds(prepared[0], prepared)

    assert left_bounds.right() <= prepared[1].source_rect.left() - 6


def test_inline_layout_stops_before_the_next_block_in_the_same_column(qtbot) -> None:
    window = OcrInlineWindow()
    qtbot.addWidget(window)
    window.resize(300, 180)
    window.add_translation(
        "first",
        _positioned_source("first", 20, 20, 150, 48),
        "第一段很长的译文需要重新排版但不能盖住下一段",
        None,
    )
    window.add_translation(
        "second",
        _positioned_source("second", 20, 70, 150, 98),
        "第二段",
        None,
    )

    prepared = window._prepare_entries()
    first_bounds = window._layout_bounds(prepared[0], prepared)

    assert first_bounds.bottom() <= prepared[1].source_rect.top() - 4


def test_inline_layout_font_always_has_a_valid_point_size(qtbot) -> None:
    window = OcrInlineWindow()
    qtbot.addWidget(window)

    layout = window._fit_layout("日本語の翻訳結果", QRectF(window.rect()), 18)

    assert layout.font().pointSizeF() > 0
