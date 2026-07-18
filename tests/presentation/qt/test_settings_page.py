from __future__ import annotations

import logging
from pathlib import Path

from PySide6.QtCore import QPoint, QPointF, Qt
from PySide6.QtGui import QWheelEvent
from PySide6.QtWidgets import QApplication, QSpinBox

from vrctranslate.application.dto import AppSettings, TranslationProfile
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.glossary import GlossaryEntry
from vrctranslate.presentation.qt.controllers.settings.translation_tester import (
    TranslationProfileTester,
)
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.translation.glossary_tab import (
    GlossaryEntryDialog,
)
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
    assert [
        page.translation_page.routes_tab.ocr_source_combo.itemData(index)
        for index in range(page.translation_page.routes_tab.ocr_source_combo.count())
    ] == ["zh-CN", "en", "ja"]
    assert set(page.ocr_page._model_install_buttons) == {"zh-CN", "ja", "en"}
    assert page.translation_page.routes_tab.self_romaji_combo.currentData() == "auto"
    assert page.translation_page.routes_tab.ocr_romaji_combo.currentData() == "off"


def test_tencent_profile_uses_secret_id_and_secret_key_labels(qtbot, tmp_path) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(
            id="tencent-test",
            name="Tencent test",
            provider="tencent",
            api_key="test-secret-id",
            model="test-secret-key",
        )
    ]

    page.load_settings(settings, str(tmp_path / "data" / "config.json"))

    assert page.translation_page.api_key_label.text() == "profile.secret_id"
    assert page.translation_page.model_label.text() == "profile.secret_key"
    assert page.translation_page.api_key_edit.text() == "test-secret-id"
    assert page.translation_page.model_edit.text() == "test-secret-key"


def test_translation_test_status_is_hidden_until_a_test_starts(qtbot, tmp_path) -> None:
    page = SettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    page.load_settings(AppSettings(), str(tmp_path / "data" / "config.json"))
    status = page.translation_page.test_status

    assert status.isHidden()

    page.translation_page.set_test_status("正在测试…")
    assert not status.isHidden()

    page.translation_page.set_test_status("")
    assert status.isHidden()


def test_profile_timeout_defaults_to_eight_seconds(
    qtbot,
    tmp_path,
) -> None:
    page = SettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(
            id="vision",
            name="Vision",
            provider="multimodal_openai",
            timeout_seconds=8,
        )
    ]
    settings.translation.ocr_route.profile_id = "vision"

    page.load_settings(settings, str(tmp_path / "data" / "config.json"))

    assert page.translation_page.timeout_spin.value() == 8.0
    assert page.translation_page.timeout_spin.minimum == 8.0


def test_switching_profiles_accepts_an_eight_second_timeout(qtbot, tmp_path) -> None:
    page = SettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(
            id="vision",
            name="Vision",
            provider="multimodal_openai",
            timeout_seconds=8,
        ),
        TranslationProfile(
            id="text",
            name="Text",
            provider="openai_compatible",
            timeout_seconds=8,
        ),
    ]
    settings.translation.ocr_route.profile_id = "vision"
    page.load_settings(settings, str(tmp_path / "data" / "config.json"))
    combo = page.translation_page.profile_combo

    combo.setCurrentIndex(combo.findData("text"))
    combo.setCurrentIndex(combo.findData("vision"))

    assert page.translation_page.selected_profile().timeout_seconds == 8.0


def test_translation_test_error_contains_reason_and_action() -> None:
    class Page:
        def set_test_status(self, _message: str, _failed: bool = False) -> None:
            return

    tester = TranslationProfileTester(
        Page(),  # type: ignore[arg-type]
        object(),  # type: ignore[arg-type]
        logging.getLogger("translation-test-message"),
        I18nManager("zh_CN"),
    )

    message = tester._failure_message(
        TranslationError("authentication", "腾讯云认证失败")
    )

    assert "测试失败：腾讯云认证失败" in message
    assert "处理建议" in message
    assert "SecretId/SecretKey" in message


def test_route_cards_share_one_field_alignment(qtbot, tmp_path) -> None:
    page = SettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    page.resize(1100, 780)
    page.load_settings(AppSettings(), str(tmp_path / "data" / "config.json"))
    routes = page.translation_page.routes_tab
    page.translation_page.tabs.setCurrentWidget(routes)
    page.show()
    qtbot.waitExposed(page)
    QApplication.processEvents()

    assert routes._self_profile_label.alignment() & Qt.AlignmentFlag.AlignRight
    assert routes._ocr_profile_label.alignment() & Qt.AlignmentFlag.AlignRight
    assert routes.self_glossary_status.x() == routes.self_romaji_help.x()
    assert routes.ocr_glossary_status.x() == routes.ocr_romaji_help.x()
    assert routes.ocr_romaji_help.x() == routes.ocr_route_warning.x()
    assert routes.self_romaji_help.objectName() == "fieldHint"
    assert routes.ocr_romaji_help.objectName() == "fieldHint"


def test_glossary_tab_shows_read_only_defaults_and_editable_user_terms(
    qtbot,
    tmp_path,
) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    builtin = (
        GlossaryEntry(
            "builtin-avatar",
            "ja",
            "zh-CN",
            "アバター",
            "虚拟形象",
            builtin=True,
        ),
        GlossaryEntry(
            "builtin-model",
            "zh-CN",
            "ja",
            "模型",
            "モデル",
            builtin=True,
        ),
    )
    user = GlossaryEntry(
        "user-avatar",
        "zh-CN",
        "ja",
        "虚拟形象",
        "アバター",
    )
    page.set_glossary_entries(builtin, (user,))
    page.load_settings(AppSettings(), str(tmp_path / "data" / "config.json"))
    tab = page.translation_page.glossary_tab

    assert page.translation_page.tabs.count() == 3
    assert tab.enabled.isChecked()
    assert tab.builtin_enabled.isChecked()
    direction_index = tab.direction_combo.findData("zh-CN\x1fja")
    assert direction_index >= 0
    assert tab.direction_combo.findData("ja\x1fzh-CN") == -1
    tab.direction_combo.setCurrentIndex(direction_index)
    assert tab.builtin_table.editTriggers() == tab.builtin_table.EditTrigger.NoEditTriggers
    assert tab.builtin_table.columnCount() == 4
    assert tab.builtin_table.rowCount() == 2
    assert tab.user_table.rowCount() == 1
    assert tab.builtin_table.item(0, 1).text() == "虚拟形象"
    assert tab.builtin_table.item(0, 2).text() == "アバター"
    assert tab.builtin_table.item(1, 1).text() == "模型"
    assert tab.builtin_table.item(1, 2).text() == "モデル"
    assert not hasattr(tab, "search")
    assert not hasattr(tab, "source_filter")
    assert not hasattr(tab, "target_filter")
    assert not hasattr(tab, "auto_notice")
    assert page.translation_page.routes_tab.self_glossary_enabled.isChecked()
    assert page.translation_page.routes_tab.ocr_glossary_enabled.isChecked()


def test_glossary_entry_dialog_only_offers_concrete_languages(qtbot) -> None:
    dialog = GlossaryEntryDialog(
        _FAKE_I18N,
        language_pair=("ja", "zh-CN"),
    )
    qtbot.addWidget(dialog)

    for combo in (dialog.source_language, dialog.target_language):
        values = [combo.itemData(index) for index in range(combo.count())]
        assert "any" not in values
    assert dialog.source_language.currentData() == "ja"
    assert dialog.target_language.currentData() == "zh-CN"


def test_builtin_glossary_collapses_reverse_rows_in_language_pair(qtbot) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    page.set_glossary_entries(
        (
            GlossaryEntry(
                "builtin-en-zh",
                "en",
                "zh-CN",
                "Quick Menu",
                "快捷菜单",
                builtin=True,
            ),
            GlossaryEntry(
                "builtin-zh-en",
                "zh-CN",
                "en",
                "快捷菜单",
                "Quick Menu",
                builtin=True,
            ),
        ),
        (),
    )
    page.load_settings(AppSettings(), "data/config.json")
    tab = page.translation_page.glossary_tab
    tab.direction_combo.setCurrentIndex(
        tab.direction_combo.findData("zh-CN\x1fen")
    )

    assert tab.builtin_table.rowCount() == 1
    assert tab.builtin_table.item(0, 1).text() == "快捷菜单"
    assert tab.builtin_table.item(0, 2).text() == "Quick Menu"


def test_glossary_actions_fit_narrow_settings_content(qtbot, tmp_path) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    page.resize(900, 560)
    page.load_settings(AppSettings(), str(tmp_path / "data" / "config.json"))
    page.show()
    qtbot.waitExposed(page)
    tab = page.translation_page.glossary_tab
    page.translation_page.tabs.setCurrentWidget(tab)
    QApplication.processEvents()

    for button in (
        tab.add_button,
        tab.edit_button,
        tab.delete_button,
        tab.copy_button,
        tab.import_button,
        tab.export_button,
    ):
        assert button.geometry().right() <= tab.contentsRect().right()
    assert tab.builtin_table.columnCount() == 4


def test_config_path_is_single_line_and_keeps_full_tooltip(qtbot) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    full_text = r"配置文件：E:\MyTools\VRCTranslate\data\config.json"
    page._location_summary.resize(220, 24)
    page._location_summary.set_full_text(full_text)

    assert "\n" not in page._location_summary.text()
    assert page._location_summary.toolTip() == full_text


def test_installed_ocr_model_hides_download_progress_and_install_action(qtbot) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    model_page = page.ocr_page

    model_page.set_model_status(
        "zh-CN",
        True,
        "PP-OCRv5-server",
        90_000_000,
        download_size=0,
        exclusive_size=84_000_000,
    )

    assert model_page._model_progress["zh-CN"].isHidden()
    assert model_page._model_install_buttons["zh-CN"].isHidden()
    assert not model_page._model_remove_buttons["zh-CN"].isHidden()
    assert model_page._model_cancel_buttons["zh-CN"].isHidden()


def test_ocr_model_download_shows_real_progress_and_only_cancel_action(qtbot) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    model_page = page.ocr_page
    model_page.set_model_status(
        "ja",
        False,
        "PP-OCRv6-medium",
        0,
        download_size=80_000_000,
        busy=True,
    )
    model_page.set_model_progress("ja", 40_000_000, 80_000_000)

    progress = model_page._model_progress["ja"]
    assert not progress.isHidden()
    assert progress.value() == 40_000_000
    assert progress.maximum() == 80_000_000
    assert model_page._model_install_buttons["ja"].isHidden()
    assert model_page._model_remove_buttons["ja"].isHidden()
    assert not model_page._model_cancel_buttons["ja"].isHidden()


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


def test_multimodal_profile_is_available_only_to_ocr_route(qtbot, tmp_path) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles.append(
        TranslationProfile(
            id="vision",
            name="Vision",
            provider="multimodal_openai",
        )
    )
    settings.translation.ocr_route.profile_id = "vision"

    page.load_settings(settings, str(tmp_path / "data" / "config.json"))

    editor = page.translation_page.profile_editor
    providers = {
        editor.provider_combo.itemData(index)
        for index in range(editor.provider_combo.count())
    }
    routes = page.translation_page.routes_tab
    assert "multimodal_openai" in providers
    assert routes.self_profile_combo.findData("vision") == -1
    assert routes.ocr_profile_combo.findData("vision") >= 0
    assert routes.ocr_route_warning.text() == "route.ocr_warning_multimodal"
    assert not routes.ocr_romaji_combo.isEnabled()
    assert routes.ocr_romaji_help.text() == "route.romaji_help_multimodal"
