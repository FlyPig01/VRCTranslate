from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow


class CaptureExcluderFake:
    def __init__(self, result: bool) -> None:
        self.result = result
        self.calls = 0

    def exclude_from_capture(self, hwnd: int) -> bool:
        self.calls += 1
        return self.result


def test_overlay_contains_only_translation_and_expires(qtbot) -> None:
    overlay = OcrOverlayWindow()
    overlay._display_seconds = 0.03
    qtbot.addWidget(overlay)
    overlay.add_translation("translated only")
    assert [text for _, text in overlay._items] == ["translated only"]
    qtbot.waitUntil(lambda: not overlay._items, timeout=1000)
    overlay.close_permanently()


def test_overlay_reports_capture_exclusion_fallback(qtbot) -> None:
    excluder = CaptureExcluderFake(False)
    overlay = OcrOverlayWindow(excluder)
    qtbot.addWidget(overlay)
    with qtbot.waitSignal(overlay.capture_exclusion_failed, timeout=1000):
        overlay.show()
    assert excluder.calls >= 1
    overlay.close_permanently()
