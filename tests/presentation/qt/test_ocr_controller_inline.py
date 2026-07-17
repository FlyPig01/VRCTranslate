from __future__ import annotations

import logging
from time import monotonic
from types import SimpleNamespace

from PySide6.QtCore import QObject

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.use_cases.ocr_translation_scheduler import (
    OcrTranslationOutcome,
)
from vrctranslate.domain.ocr import OcrText, WindowInfo
from vrctranslate.domain.translation import TranslationResult
from vrctranslate.presentation.qt.controllers.ocr_controller import (
    OcrController,
    _PendingInlineLayout,
)


class _OverlayRecorder:
    def __init__(self) -> None:
        self.items: list[tuple[str, str]] = []
        self.visible = False

    def add_translation(self, original: str, translated: str) -> None:
        self.items.append((original, translated))

    def isVisible(self) -> bool:
        return self.visible

    def show(self) -> None:
        self.visible = True


class _InlineRecorder:
    def __init__(self) -> None:
        self.items: list[tuple[str, OcrText, str, float | None]] = []
        self.clear_count = 0

    def add_translation(
        self,
        request_id: str,
        source: OcrText,
        translated: str,
        display_seconds: float | None,
    ) -> None:
        self.items.append((request_id, source, translated, display_seconds))

    def clear(self) -> None:
        self.clear_count += 1


class _PageRecorder:
    def __init__(self) -> None:
        self.last_translation: tuple[str, str] | None = None
        self.status = ""

    def set_last_translation(self, original: str, translated: str) -> None:
        self.last_translation = (original, translated)

    def set_status(self, message: str) -> None:
        self.status = message


class _Target:
    def __init__(self, window: WindowInfo) -> None:
        self.window = window

    def selected_window(self) -> WindowInfo:
        return self.window


def _source() -> OcrText:
    box = ((20, 24), (180, 24), (180, 54), (20, 54))
    return OcrText("hello", 0.95, box, (box,), (320, 180), 0.2)


def _controller(mode: str) -> tuple[OcrController, _OverlayRecorder, _InlineRecorder]:
    controller = OcrController.__new__(OcrController)
    QObject.__init__(controller)
    settings = AppSettings()
    settings.ui.ocr_display_mode = mode
    overlay = _OverlayRecorder()
    inline = _InlineRecorder()
    controller._settings = SimpleNamespace(current=settings)
    controller._overlay = overlay
    controller._inline = inline
    controller._page = _PageRecorder()
    controller._target = _Target(WindowInfo(12, "VRChat", 0, 0, 800, 600))
    controller._logger = logging.getLogger("test-ocr-controller-inline")
    controller._i18n = None
    controller._ocr_active = True
    controller._single_capture_finished = False
    controller._inline_available = True
    controller._layout_generation = 3
    controller._pending_inline = {}
    return controller, overlay, inline


def _complete(controller: OcrController, request_id: str = "request") -> None:
    controller._translation_completed(
        OcrTranslationOutcome(
            request_id,
            result=TranslationResult(
                request_id,
                "hello",
                "你好",
                "en",
                "zh-CN",
                "ocr",
            ),
        )
    )


def _add_pending(
    controller: OcrController,
    *,
    request_id: str = "request",
    generation: int = 3,
    created_at: float | None = None,
) -> None:
    controller._pending_inline[request_id] = _PendingInlineLayout(
        _source(),
        generation,
        12,
        monotonic() if created_at is None else created_at,
    )


def test_inline_mode_sends_a_valid_result_only_to_inline_surface(qtbot) -> None:
    del qtbot
    controller, overlay, inline = _controller("inline")
    _add_pending(controller)

    _complete(controller)

    assert overlay.items == []
    assert [(item[0], item[2]) for item in inline.items] == [("request", "你好")]


def test_both_mode_sends_a_valid_result_to_both_surfaces(qtbot) -> None:
    del qtbot
    controller, overlay, inline = _controller("both")
    _add_pending(controller)

    _complete(controller)

    assert overlay.items == [("hello", "你好")]
    assert [(item[0], item[2]) for item in inline.items] == [("request", "你好")]


def test_stale_layout_is_not_drawn_inline_and_falls_back_to_overlay(qtbot) -> None:
    del qtbot
    controller, overlay, inline = _controller("inline")
    _add_pending(controller, generation=2)

    _complete(controller)

    assert inline.items == []
    assert overlay.items == [("hello", "你好")]


def test_capture_exclusion_failure_disables_inline_and_uses_overlay(qtbot) -> None:
    del qtbot
    controller, overlay, inline = _controller("inline")
    _add_pending(controller)

    controller._inline_exclusion_warning()
    _complete(controller)

    assert controller._inline_available is False
    assert inline.clear_count == 1
    assert inline.items == []
    assert overlay.items == [("hello", "你好")]


def test_single_mode_inline_result_has_no_time_expiry(qtbot) -> None:
    del qtbot
    controller, overlay, inline = _controller("inline")
    controller._settings.current.ocr.recognition_mode = "single"
    _add_pending(controller)

    _complete(controller)

    assert overlay.items == []
    assert inline.items[0][3] is None
