from __future__ import annotations

from pathlib import Path

from PySide6.QtCore import QPoint, QPointF, Qt
from PySide6.QtGui import QWheelEvent
from PySide6.QtWidgets import QApplication, QSpinBox

from vrctranslate.application.dto import AppSettings
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.widgets.no_wheel_combobox import NoWheelComboBox
from vrctranslate.presentation.qt.widgets.numeric_line_edit import NumericLineEdit


class _FakeI18n:
    def tr(self, key: str, **kwargs) -> str:
        return key

    @property
    def language_changed(self):
        return _FakeSignal()


class _FakeSignal:
    def connect(self, *args):
        pass


_FAKE_I18N = _FakeI18n()


def test_settings_has_four_discoverable_sections_and_fixed_save(qtbot, tmp_path) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    page.resize(720, 520)
    page.load_settings(
        AppSettings(),
        str(tmp_path / "data" / "config.json"),
    )
    page.show()
    qtbot.waitExposed(page)

    assert page.section_nav.count() == 4
    assert [page.section_nav.tabText(i) for i in range(4)] == [
        "settings.section.translation",
        "settings.section.osc",
        "settings.section.ocr",
        "settings.section.data",
    ]
    assert page._save_button.isVisible()
    assert not page.findChildren(QSpinBox)


def test_numeric_input_has_no_wheel_or_arrow_stepping(qtbot) -> None:
    edit = NumericLineEdit(1, 100)
    qtbot.addWidget(edit)
    edit.setValue(25)
    edit.show()
    edit.setFocus()

    qtbot.keyPress(edit, Qt.Key.Key_Up)
    qtbot.keyPress(edit, Qt.Key.Key_Down)
    wheel = QWheelEvent(
        QPointF(5, 5),
        QPointF(5, 5),
        QPoint(0, 0),
        QPoint(0, 120),
        Qt.MouseButton.NoButton,
        Qt.KeyboardModifier.NoModifier,
        Qt.ScrollPhase.ScrollUpdate,
        False,
    )
    QApplication.sendEvent(edit, wheel)
    assert edit.value() == 25


def test_combobox_draws_a_visible_down_arrow(qtbot) -> None:
    combo = NoWheelComboBox()
    qtbot.addWidget(combo)
    combo.addItems(["VRChat", "Another application"])
    combo.resize(240, 38)
    combo.show()
    qtbot.waitExposed(combo)

    image = combo.grab().toImage()
    arrow_pixels = 0
    for y in range(image.height()):
        for x in range(max(0, image.width() - 32), image.width()):
            color = image.pixelColor(x, y)
            if color.blue() - color.red() > 50 and color.green() - color.red() > 40:
                arrow_pixels += 1
    assert arrow_pixels > 0


def test_ui_svg_icons_are_runtime_resources() -> None:
    for name in (
        "ui/nav_input.svg",
        "ui/nav_ocr.svg",
        "ui/nav_settings.svg",
        "ui/settings_translation.svg",
        "ui/settings_osc.svg",
        "ui/settings_ocr.svg",
        "ui/settings_data.svg",
        "ui/action_save.svg",
        "ui/ocr_orb_idle.svg",
        "ui/ocr_orb_running.svg",
        "ui/ocr_orb_waiting.svg",
        "ui/ocr_orb_error.svg",
    ):
        assert not load_icon(name).isNull(), name


def test_runtime_svg_files_match_editable_design_sources() -> None:
    root = Path(__file__).parents[3]
    runtime = root / "src" / "vrctranslate" / "presentation" / "qt" / "resources" / "icons" / "ui"
    for source in (root / "assets" / "icons").glob("*.svg"):
        target = runtime / source.name
        if target.exists():
            assert target.read_text(encoding="utf-8") == source.read_text(encoding="utf-8")


def test_quick_input_overlay_settings_live_on_quick_input_page(qtbot) -> None:
    page = SelfMessagePage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.ui.input_width = 510
    page.load_ui_settings(settings.ui)

    assert page.input_width_edit.value() == 510
    assert not page.has_unsaved_changes
    changes: list[tuple[bool, int]] = []
    page.input_settings_changed.connect(
        lambda topmost, width: changes.append((topmost, width))
    )
    page.input_width_edit.setValue(620)
    assert changes[-1] == (settings.ui.input_topmost, 620)
    assert not page.has_unsaved_changes
    assert not hasattr(page, "save_bar")
    assert not hasattr(page, "_show_button")

    settings_page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(settings_page)
    assert not hasattr(settings_page.osc_page, "input_width_spin")
    assert not hasattr(settings_page.ocr_page, "overlay_opacity_spin")
