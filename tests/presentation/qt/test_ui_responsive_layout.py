from __future__ import annotations

import logging

import pytest
from PySide6.QtCore import QPoint, QRect, Qt
from PySide6.QtGui import QColor, QImage, QPainter
from PySide6.QtWidgets import QListWidget, QStyle, QStyleOptionButton, QTableWidget

from vrctranslate.application.dto import AppSettings
from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.presentation.qt.app_style import VrcTranslateStyle
from vrctranslate.presentation.qt.main_window import MainWindow
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow
from vrctranslate.presentation.qt.windows.quick_input_window import QuickInputWindow


class _Signal:
    def connect(self, *_: object) -> None:
        return None


class _I18n:
    language_changed = _Signal()

    def tr(self, key: str, **kwargs: object) -> str:
        if not kwargs:
            return key
        return f"{key}:{kwargs}"


class _Settings:
    def __init__(self) -> None:
        self.current = AppSettings()

    def save(self, settings: AppSettings) -> None:
        self.current = settings


I18N = _I18n()


def _top_left(widget, parent) -> QPoint:  # noqa: ANN001
    return widget.mapTo(parent, QPoint(0, 0))


def test_overlay_settings_use_percentage_and_round_trip(qtbot, tmp_path) -> None:
    page = OcrPage(I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.ui.ocr_overlay_opacity = 0.63
    settings.ui.ocr_overlay_show_original = False
    settings.ui.ocr_overlay_font_size = 19
    settings.ui.ocr_display_mode = "inline"
    settings.ui.ocr_inline_opacity = 0.78
    page.load_settings(settings)

    assert page.overlay_opacity_spin.value() == 63
    assert not page.ocr_show_original_check.isChecked()
    assert page.display_mode_combo.currentData() == "inline"
    assert page.inline_opacity_spin.value() == 78
    page.overlay_opacity_spin.setValue(72)
    page.ocr_show_original_check.setChecked(True)
    page.display_mode_combo.setCurrentIndex(page.display_mode_combo.findData("both"))
    page.inline_opacity_spin.setValue(86)

    page.collect_ui_settings(settings.ui)
    assert settings.ui.ocr_overlay_opacity == pytest.approx(0.72)
    assert settings.ui.ocr_overlay_show_original is True
    assert settings.ui.ocr_overlay_font_size == 19
    assert settings.ui.ocr_display_mode == "both"
    assert settings.ui.ocr_inline_opacity == pytest.approx(0.86)


def test_overlay_live_preview_reloads_saved_values(qtbot, tmp_path) -> None:
    page = OcrPage(I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.ui.ocr_overlay_opacity = 0.82
    settings.ui.ocr_overlay_font_size = 18
    settings.ui.ocr_overlay_show_original = True
    received: list[tuple[float, int, bool]] = []
    page.overlay_preview_changed.connect(
        lambda opacity, font, original: received.append(
            (opacity, font, original)
        )
    )
    page.load_settings(settings)
    received.clear()

    page.overlay_opacity_spin.setValue(55)
    page.overlay_font_spin.setValue(21)
    page.ocr_show_original_check.setChecked(False)
    assert received[-1] == (0.55, 21, False)

    page.load_settings(settings)
    assert page.overlay_opacity_spin.value() == 82
    assert page.overlay_font_spin.value() == 18
    assert page.ocr_show_original_check.isChecked()


def test_overlay_position_actions_are_visible_and_forwarded(qtbot, tmp_path) -> None:
    page = OcrPage(I18N)
    qtbot.addWidget(page)
    page.load_settings(AppSettings())
    shown: list[bool] = []
    reset: list[bool] = []
    page.overlay_show_requested.connect(lambda: shown.append(True))
    page.overlay_reset_requested.connect(lambda: reset.append(True))

    qtbot.mouseClick(page.show_overlay_button, Qt.MouseButton.LeftButton)
    qtbot.mouseClick(page.reset_overlay_button, Qt.MouseButton.LeftButton)
    assert shown == [True]
    assert reset == [True]

    page.set_overlay_geometry(100, 80, 500, 260)
    summary = page.overlay_geometry_summary.text()
    assert "100" in summary
    assert "500" in summary
    assert "260" in summary


def test_settings_navigation_is_vertical_and_save_remains_visible(qtbot, tmp_path) -> None:
    page = SettingsPage(I18N)
    qtbot.addWidget(page)
    page.resize(720, 520)
    page.load_settings(
        AppSettings(),
        str(tmp_path / "config.json"),
    )
    page.show()
    qtbot.waitExposed(page)

    assert isinstance(page.section_nav, QListWidget)
    assert page.section_nav.width() == 176
    first = page.section_nav.visualItemRect(page.section_nav.item(0))
    second = page.section_nav.visualItemRect(page.section_nav.item(1))
    assert first.top() < second.top()
    assert page._save_button.isVisible()
    assert _top_left(page._save_button, page).y() < _top_left(page.section_stack, page).y()


def test_ocr_overlay_choices_are_on_ocr_feature_page(qtbot, tmp_path) -> None:
    page = OcrPage(I18N)
    qtbot.addWidget(page)
    page.resize(900, 560)
    page.load_settings(AppSettings())
    page.show()
    qtbot.waitExposed(page)

    panel = page.overlay_choice_panel
    assert page.ocr_topmost_check.parentWidget() is panel
    assert page.ocr_passthrough_check.parentWidget() is panel
    assert page.ocr_show_original_check.parentWidget() is panel


def test_checked_indicator_uses_a_white_mark(qapp) -> None:
    image = QImage(24, 24, QImage.Format.Format_ARGB32_Premultiplied)
    image.fill(Qt.GlobalColor.transparent)
    painter = QPainter(image)
    option = QStyleOptionButton()
    option.rect = QRect(2, 2, 20, 20)
    option.state = QStyle.StateFlag.State_Enabled | QStyle.StateFlag.State_On
    style = VrcTranslateStyle()
    style.drawPrimitive(
        QStyle.PrimitiveElement.PE_IndicatorCheckBox,
        option,
        painter,
    )
    painter.end()

    white_pixels = 0
    for y in range(image.height()):
        for x in range(image.width()):
            color = QColor(image.pixelColor(x, y))
            if color.alpha() > 200 and min(color.red(), color.green(), color.blue()) > 240:
                white_pixels += 1
    assert white_pixels > 0


def test_feature_pages_reflow_without_horizontal_clipping(qtbot) -> None:
    ocr_page = OcrPage(I18N)
    self_page = SelfMessagePage(I18N)
    for page in (ocr_page, self_page):
        qtbot.addWidget(page)
        page.resize(724, 560)
        page.show()
        qtbot.waitExposed(page)
        assert page._narrow_layout is True
        assert page._scroll.horizontalScrollBar().maximum() == 0

    button_left = ocr_page.refresh_targets_button.mapTo(
        ocr_page._scroll.viewport(), QPoint(0, 0)
    ).x()
    assert button_left >= 0
    assert (
        button_left + ocr_page.refresh_targets_button.width()
        <= ocr_page._scroll.viewport().width()
    )

    for page in (ocr_page, self_page):
        page.resize(1200, 700)
        qtbot.wait(20)
        assert page._narrow_layout is False


@pytest.mark.parametrize("size", [(900, 560), (960, 660), (1200, 800)])
def test_ocr_page_has_read_only_summary_and_no_legacy_controls(qtbot, size) -> None:
    page = OcrPage(I18N)
    qtbot.addWidget(page)
    page.resize(*size)
    page.show()
    qtbot.waitExposed(page)

    assert not hasattr(page, "window_combo")
    assert not hasattr(page, "_region_button")
    assert not hasattr(page, "_toggle_button")
    assert not page.findChildren(QTableWidget)
    assert page.target_combo.isVisible()
    assert page.refresh_targets_button.isVisible()
    assert not hasattr(page, "show_orb_button")
    assert not hasattr(page, "save_bar")
    page.set_runtime_summary(
        mode="continuous",
        language="ja → zh-CN",
        profile="Google",
    )
    page.set_target_windows(
        [WindowInfo(10, "VRChat", 0, 0, 1280, 720, "VRChat.exe")],
        10,
    )
    assert "VRChat" in page.target_combo.currentText()
    assert "ja → zh-CN" in page._language_label.text()
    page.set_target_windows(
        [WindowInfo(10, "VRChat", 0, 0, 1280, 720, "VRChat.exe")],
        None,
    )
    assert page.selected_hwnd is None


def test_main_sidebar_is_always_complete(qtbot) -> None:
    settings = _Settings()
    self_page = SelfMessagePage(I18N)
    ocr_page = OcrPage(I18N)
    settings_page = SettingsPage(I18N)
    quick = QuickInputWindow(i18n=I18N)
    overlay = OcrOverlayWindow(i18n=I18N)
    for widget in (quick, overlay):
        qtbot.addWidget(widget)
    window = MainWindow(
        self_page,
        ocr_page,
        settings_page,
        quick,
        overlay,
        settings,  # type: ignore[arg-type]
        logging.getLogger("test-ui-layout"),
        I18N,  # type: ignore[arg-type]
    )
    qtbot.addWidget(window)

    window.resize(900, 560)
    window._update_sidebar_mode()
    assert window._sidebar.width() == 176
    assert not window._brand_name.isHidden()
    assert all(window.navigation.item(i).text() for i in range(3))

    window.resize(1000, 660)
    window._update_sidebar_mode()
    assert window._sidebar.width() == 176
    assert not window._brand_name.isHidden()
    assert all(window.navigation.item(i).text() for i in range(3))
