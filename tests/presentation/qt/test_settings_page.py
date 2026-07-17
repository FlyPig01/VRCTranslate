from __future__ import annotations

from pathlib import Path

from PySide6.QtCore import QPoint, QPointF, Qt
from PySide6.QtGui import QWheelEvent
from PySide6.QtWidgets import QApplication, QSpinBox

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.local_models import LocalTranslationModel
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
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
        False,
        str(tmp_path / "data" / "models" / "argos"),
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
    ):
        assert not load_icon(name).isNull(), name


def test_runtime_svg_files_match_editable_design_sources() -> None:
    root = Path(__file__).parents[3]
    runtime = root / "src" / "vrctranslate" / "presentation" / "qt" / "resources" / "icons" / "ui"
    for source in (root / "assets" / "icons").glob("*.svg"):
        target = runtime / source.name
        if target.exists():
            assert target.read_text(encoding="utf-8") == source.read_text(encoding="utf-8")


def test_argos_catalog_uses_readable_language_names_and_route_target(qtbot, tmp_path) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.ocr_route.target_language = "zh-CN"
    page.load_settings(
        settings,
        str(tmp_path / "data" / "config.json"),
        True,
        str(tmp_path / "data" / "models" / "argos"),
    )
    models = [
        LocalTranslationModel("en", "zh", "1.0"),
        LocalTranslationModel("ja", "en", "1.1"),
    ]

    page.set_model_catalog("ready", [], models, 0)
    translation = page.translation_page

    assert translation.argos_target_filter.currentData() == "zh"
    assert "argos_lang.zh（zh）" in translation.argos_target_filter.currentText()
    assert "argos_lang.en（en） → argos_lang.zh（zh）" in translation.available_model_combo.currentText()
    assert "argos.ready_install" in translation.argos_selection_summary.text()
