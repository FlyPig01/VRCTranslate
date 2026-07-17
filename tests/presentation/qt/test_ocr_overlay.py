from pathlib import Path

from PySide6.QtCore import QEvent, QObject, QPoint, QSize, Qt
from PySide6.QtGui import QColor, QGuiApplication, QImage, QPainter, QPalette
from PySide6.QtTest import QSignalSpy
from PySide6.QtWidgets import QApplication, QLabel, QWidget

from vrctranslate.application.dto import UiSettings
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow


class _ParentlessOriginalShowRecorder(QObject):
    def __init__(self) -> None:
        super().__init__()
        self.parentless_shows = 0

    def eventFilter(self, watched, event):  # noqa: ANN001
        if (
            event.type() == QEvent.Type.Show
            and isinstance(watched, QLabel)
            and watched.objectName() == "ocrOriginal"
            and watched.parentWidget() is None
        ):
            self.parentless_shows += 1
        return False


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
    overlay.add_translation("hello", "こんにちは")
    assert [(orig, trans) for _, orig, trans in overlay._items] == [("hello", "こんにちは")]
    qtbot.waitUntil(lambda: not overlay._items, timeout=1000)
    overlay.close_permanently()


def test_translation_labels_are_parented_before_becoming_visible(qtbot) -> None:
    application = QApplication.instance()
    assert application is not None
    recorder = _ParentlessOriginalShowRecorder()
    application.installEventFilter(recorder)
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    try:
        overlay.add_translation("hello", "你好")
        overlay.show()
        qtbot.waitExposed(overlay)
        qtbot.waitUntil(
            lambda: overlay.findChild(QLabel, "ocrOriginal") is not None
        )
        original = overlay.findChild(QLabel, "ocrOriginal")
        translated = overlay.findChild(QLabel, "ocrTranslation")
        assert original is not None and translated is not None
        assert original.font().pointSizeF() > 0
        assert translated.font().pointSizeF() > 0
        assert original.parentWidget() is not None
        assert translated.parentWidget() is not None
        assert not original.isWindow()
        assert not translated.isWindow()
        assert recorder.parentless_shows == 0
    finally:
        application.removeEventFilter(recorder)
        overlay.close_permanently()


def test_overlay_reports_capture_exclusion_fallback(qtbot) -> None:
    excluder = CaptureExcluderFake(False)
    overlay = OcrOverlayWindow(excluder)
    qtbot.addWidget(overlay)
    with qtbot.waitSignal(overlay.capture_exclusion_failed, timeout=1000):
        overlay.show()
    assert excluder.calls >= 1
    overlay.close_permanently()


def test_overlay_uses_one_transparent_surface_and_applies_background_opacity(qtbot) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    settings = UiSettings(
        ocr_overlay_opacity=0.35,
        ocr_overlay_show_original=False,
    )
    overlay.apply_settings(settings)
    overlay.show()
    qtbot.waitExposed(overlay)

    assert overlay.testAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
    assert overlay.mask().isEmpty()
    assert overlay.surface.background_opacity == 0.35
    assert not overlay._show_original

    image = QImage(overlay.size(), QImage.Format.Format_ARGB32_Premultiplied)
    image.fill(QColor(0, 0, 0, 0))
    painter = QPainter(image)
    overlay.render(painter, QPoint())
    painter.end()
    assert image.pixelColor(0, 0).alpha() == 0
    assert image.pixelColor(overlay.width() // 2, overlay.height() // 2).alpha() > 0
    overlay.close_permanently()


def test_overlay_geometry_is_committed_after_movement_quiets(qtbot) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    overlay.show()
    qtbot.waitExposed(overlay)
    overlay._geometry_commit_timer.stop()
    spy = QSignalSpy(overlay.geometry_changed)

    overlay.move(overlay.x() + 20, overlay.y() + 15)
    qtbot.wait(200)
    assert spy.count() == 0
    qtbot.waitUntil(lambda: spy.count() == 1, timeout=1500)
    overlay.close_permanently()


def test_drag_handle_prefers_native_system_move(qtbot, monkeypatch) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    overlay.show()
    qtbot.waitExposed(overlay)
    calls = []
    monkeypatch.setattr(
        overlay.drag_handle,
        "_start_system_move",
        lambda: calls.append(True) or True,
    )

    qtbot.mousePress(overlay.drag_handle, Qt.MouseButton.LeftButton)

    assert calls == [True]
    overlay.close_permanently()


def test_translation_items_are_reused_when_a_new_result_arrives(qtbot) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    overlay.add_translation("first", "第一条")
    qtbot.waitUntil(lambda: len(overlay._entry_widgets) == 1)
    first_id = overlay._model.entries[0].entry_id
    first_widget = overlay._entry_widgets[first_id]

    overlay.add_translation("second", "第二条")

    qtbot.waitUntil(lambda: len(overlay._entry_widgets) == 2)
    assert overlay._entry_widgets[first_id] is first_widget
    overlay.close_permanently()


def test_results_are_coalesced_while_overlay_is_being_dragged(qtbot) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    overlay.add_translation("existing", "已有译文")
    qtbot.waitUntil(lambda: len(overlay._entry_widgets) == 1)
    existing_id = overlay._model.entries[0].entry_id
    existing_widget = overlay._entry_widgets[existing_id]

    overlay.drag_handle.interaction_started.emit()
    overlay.add_translation("intermediate", "中间结果")
    overlay.add_translation("latest", "最新结果")
    qtbot.wait(30)

    assert len(overlay._model.entries) == 3
    assert len(overlay._entry_widgets) == 1
    assert overlay._entry_widgets[existing_id] is existing_widget

    overlay.drag_handle.interaction_finished.emit()
    qtbot.waitUntil(lambda: len(overlay._entry_widgets) == 3)
    assert overlay._entry_widgets[existing_id] is existing_widget
    overlay.close_permanently()


def test_quiet_geometry_timer_flushes_results_after_native_move(qtbot) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    overlay._geometry_commit_timer.setInterval(20)
    overlay.drag_handle.interaction_started.emit()
    overlay.add_translation("during native move", "原生移动期间")
    assert not overlay._entry_widgets

    overlay._schedule_geometry_commit()

    qtbot.waitUntil(lambda: len(overlay._entry_widgets) == 1, timeout=1000)
    assert not overlay._interaction_active
    overlay.close_permanently()


def test_resize_grip_defers_results_until_resize_finishes(qtbot) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    overlay.size_grip.interaction_started.emit()
    overlay.add_translation("during resize", "缩放期间")
    qtbot.wait(30)
    assert not overlay._entry_widgets

    overlay.size_grip.interaction_finished.emit()

    qtbot.waitUntil(lambda: len(overlay._entry_widgets) == 1)
    overlay.close_permanently()


def test_overlay_geometry_can_be_reset_to_portable_default(qtbot) -> None:
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    overlay.resize(700, 400)
    overlay.move(10, 10)

    overlay.reset_geometry()

    assert overlay.size() == QSize(420, 220)
    screen = QGuiApplication.primaryScreen()
    assert screen is not None
    assert screen.availableGeometry().contains(overlay.geometry())
    overlay.close_permanently()


def test_translation_card_keeps_high_contrast_on_light_and_dark_backgrounds(
    qtbot,
) -> None:
    application = QApplication.instance()
    assert application is not None
    previous_style = application.styleSheet()
    stylesheet = (
        Path(__file__).parents[3]
        / "src/vrctranslate/presentation/qt/resources/styles/ocr_overlay.qss"
    ).read_text(encoding="utf-8")
    application.setStyleSheet(stylesheet)
    overlay = OcrOverlayWindow()
    qtbot.addWidget(overlay)
    try:
        overlay.add_translation("recognized original", "Readable translation")
        overlay.show()
        qtbot.waitExposed(overlay)
        qtbot.waitUntil(
            lambda: overlay.findChild(QWidget, "ocrTranslationItem") is not None
        )
        original = overlay.findChild(QLabel, "ocrOriginal")
        translated = overlay.findChild(QLabel, "ocrTranslation")
        assert original is not None and translated is not None
        assert original.palette().color(QPalette.ColorRole.WindowText) == QColor(
            "#d8e8f6"
        )
        assert translated.palette().color(QPalette.ColorRole.WindowText) == QColor(
            "#ffffff"
        )

        item = overlay.findChild(QWidget, "ocrTranslationItem")
        image = item.grab().toImage()
        colors = [
            image.pixelColor(x, y)
            for y in range(3, max(4, image.height() - 3))
            for x in range(3, max(4, image.width() - 3))
            if image.pixelColor(x, y).alpha() >= 200
        ]
        assert colors
        luminance = lambda color: (
            0.2126 * color.redF()
            + 0.7152 * color.greenF()
            + 0.0722 * color.blueF()
        )
        values = [luminance(color) for color in colors]
        assert (max(values) + 0.05) / (min(values) + 0.05) >= 7
    finally:
        overlay.close_permanently()
        application.setStyleSheet(previous_style)
