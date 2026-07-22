from __future__ import annotations

import logging
from pathlib import Path

from PySide6.QtCore import QPoint, QPointF, Qt
from PySide6.QtGui import QWheelEvent
from PySide6.QtWidgets import (
    QApplication,
    QDialog,
    QFrame,
    QLabel,
    QScrollArea,
    QSpinBox,
    QTreeWidget,
)

from vrctranslate.application.dto import (
    AppSettings,
    SpeechRecognitionProfile,
    TranslationProfile,
    TranslationSettings,
)
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
from vrctranslate.presentation.qt.pages.settings.translation.add_profile_dialog import (
    AddProfileDialog,
)
from vrctranslate.presentation.qt.pages.settings.translation.routes_tab import RoutesTab
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


def test_settings_has_five_discoverable_sections_and_fixed_save(qtbot, tmp_path) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    page.resize(720, 520)
    page.load_settings(
        AppSettings(),
        str(tmp_path / "data" / "config.json"),
    )
    page.show()
    qtbot.waitExposed(page)

    assert page.section_nav.count() == 5
    assert not hasattr(page, "_subtitle")
    assert [page.section_nav.tabText(i) for i in range(5)] == [
        "settings.section.translation",
        "settings.section.osc",
        "settings.section.ocr",
        "settings.section.voice",
        "settings.section.data",
    ]
    assert page._save_button.isVisible()
    assert not page.findChildren(QSpinBox)
    assert [
        page.translation_page.routes_tab.ocr_source_combo.itemData(index)
        for index in range(page.translation_page.routes_tab.ocr_source_combo.count())
        ] == [
            "auto",
            "zh-CN",
            "zh-TW",
            "en",
            "ja",
            "ko",
            "fr",
            "de",
            "es",
            "ru",
        ]
    assert set(page.ocr_page._model_install_buttons) == {
        "zh-CN",
        "ja",
        "en",
        "ko",
        "latin",
        "cyrillic",
    }
    assert page.translation_page.routes_tab.self_romaji_combo.currentData() == "auto"
    assert page.translation_page.routes_tab.ocr_romaji_combo.currentData() == "off"


def test_interface_language_uses_a_native_name_dropdown(qtbot) -> None:
    page = SettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    page.load_settings(AppSettings(), "data/config.json")

    assert not hasattr(page, "_lang_btn")
    assert [
        page._lang_combo.itemData(index)
        for index in range(page._lang_combo.count())
    ] == [
        "zh_CN",
        "zh_TW",
        "en_US",
        "ja_JP",
        "ko_KR",
        "fr_FR",
        "de_DE",
        "es_ES",
        "ru_RU",
    ]
    assert page._lang_combo.itemText(4) == "한국어"


def test_route_languages_follow_translation_and_speech_profiles(qtbot) -> None:
    tab = RoutesTab(I18nManager("zh_CN"))
    qtbot.addWidget(tab)
    settings = TranslationSettings(
        profiles=[
            TranslationProfile(
                id="tencent",
                name="Tencent",
                provider="tencent",
            )
        ]
    )
    settings.self_route.profile_id = "tencent"
    settings.ocr_route.profile_id = "tencent"
    settings.voice_route.profile_id = "tencent"
    tab.load_settings(settings)

    self_sources = {
        tab.self_source_combo.itemData(index)
        for index in range(tab.self_source_combo.count())
    }
    assert "auto" not in self_sources
    assert "ko" in self_sources

    tab.set_speech_profile(
        SpeechRecognitionProfile(
            provider="tencent_realtime",
            model="16k_ja",
        )
    )
    assert [
        tab.voice_source_combo.itemData(index)
        for index in range(tab.voice_source_combo.count())
    ] == ["ja"]


def test_route_quality_hint_is_advisory_and_keeps_current_profile(qtbot) -> None:
    tab = RoutesTab(I18nManager("zh_CN"))
    qtbot.addWidget(tab)
    settings = TranslationSettings(
        profiles=[
            TranslationProfile(
                id="tencent",
                name="Tencent",
                provider="tencent",
            ),
            TranslationProfile(
                id="aliyun",
                name="Aliyun",
                provider="aliyun",
                options={"aliyun_api": "general"},
            ),
        ]
    )
    settings.self_route.profile_id = "tencent"
    settings.self_route.source_language = "en"
    settings.self_route.target_language = "zh-CN"

    tab.load_settings(settings)

    assert not tab.self_quality_hint.isHidden()
    assert "当前档案是此方向的候选" in tab.self_quality_hint.text()
    assert tab.self_profile_combo.currentData() == "tencent"

    tab.self_source_combo.setCurrentIndex(
        tab.self_source_combo.findData("zh-CN")
    )
    tab.self_target_combo.setCurrentIndex(
        tab.self_target_combo.findData("en")
    )

    assert "阿里云" in tab.self_quality_hint.text()
    assert tab.self_profile_combo.currentData() == "tencent"


def test_google_free_route_is_marked_experimental(qtbot) -> None:
    tab = RoutesTab(I18nManager("zh_CN"))
    qtbot.addWidget(tab)
    settings = TranslationSettings(
        profiles=[
            TranslationProfile(
                id="google",
                name="Google free",
                provider="google_free",
            )
        ]
    )
    settings.self_route.profile_id = "google"
    settings.self_route.source_language = "en"
    settings.self_route.target_language = "zh-CN"

    tab.load_settings(settings)

    assert "不建议用于实时默认路线" in tab.self_quality_hint.text()
    assert tab.self_quality_hint.property("state") == "experimental"


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
    assert routes._voice_profile_label.alignment() & Qt.AlignmentFlag.AlignRight
    assert routes.self_glossary_status.x() == routes.self_romaji_help.x()
    assert routes.ocr_glossary_status.x() == routes.ocr_romaji_help.x()
    assert routes.ocr_romaji_help.x() == routes.ocr_route_warning.x()
    assert routes.self_romaji_help.objectName() == "fieldHint"
    assert routes.ocr_romaji_help.objectName() == "fieldHint"


def test_voice_translation_route_is_owned_by_the_unified_routes_page(
    qtbot,
    tmp_path,
) -> None:
    page = SettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles.append(
        TranslationProfile(
            id="translator",
            name="Translator",
            provider="openai_compatible",
        )
    )
    settings.translation.voice_route.profile_id = "translator"
    settings.translation.voice_route.source_language = "ja"
    settings.translation.voice_route.target_language = "en"
    settings.translation.voice_route.glossary_enabled = False

    page.load_settings(settings, str(tmp_path / "data" / "config.json"))
    routes = page.translation_page.routes_tab

    assert routes._voice_card_title.text() == "他人语音（识别后翻译）"
    assert routes.voice_profile_combo.currentData() == "translator"
    assert routes.voice_source_combo.currentData() == "ja"
    assert routes.voice_target_combo.currentData() == "en"
    assert not routes.voice_glossary_enabled.isChecked()

    routes.voice_target_combo.setCurrentIndex(
        routes.voice_target_combo.findData("zh-CN")
    )
    collected = page.collect_settings(settings)

    assert collected.translation.voice_route.profile_id == "translator"
    assert collected.translation.voice_route.source_language == "ja"
    assert collected.translation.voice_route.target_language == "zh-CN"
    assert collected.translation.voice_route.glossary_enabled is False


def test_voice_route_is_always_recognition_then_text_translation(qtbot) -> None:
    routes = RoutesTab(I18nManager("zh_CN"))
    qtbot.addWidget(routes)

    settings = TranslationSettings()
    settings.voice_route.translation_strategy = "native_voice"
    routes.load_settings(settings)
    routes.collect_settings(settings)

    assert settings.voice_route.translation_strategy == "text_profile"
    assert routes.voice_profile_combo.isEnabled()
    assert not hasattr(routes, "voice_strategy_combo")


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


def test_ocr_model_manager_uses_one_scalable_detail_card(qtbot) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    model_page = page.ocr_page

    cards = model_page.findChildren(QFrame, "ocrModelCard")
    assert len(cards) == 1
    assert model_page.model_selector.count() == 6

    model_page.set_model_status(
        "en",
        True,
        "PP-OCRv5-mobile",
        8_000_000,
        exclusive_size=8_000_000,
    )
    installed_filter = model_page.model_filter_combo.findData("installed")
    model_page.model_filter_combo.setCurrentIndex(installed_filter)

    assert model_page.model_selector.count() == 1
    assert model_page.model_selector.currentData() == "en"
    assert model_page._model_install_summary.text() == (
        "ocr_models.installed_count"
    )


def test_ocr_model_action_targets_selected_model(qtbot) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    model_page = page.ocr_page
    requested: list[str] = []
    model_page.model_install_requested.connect(requested.append)

    model_page.model_selector.setCurrentIndex(
        model_page.model_selector.findData("ja")
    )
    model_page._model_install_button.click()

    assert requested == ["ja"]


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
    settings.translation.self_route.message_format = "original_then_translation"
    page.load_ui_settings(settings.ui)
    page.load_route_settings(settings.translation.self_route)

    assert page.input_width_edit.value() == 510
    assert page.message_format_combo.currentData() == "original_then_translation"
    assert not page.has_unsaved_changes
    changes: list[tuple[bool, int, str, str]] = []
    page.input_settings_changed.connect(
        lambda topmost, width, message_format, hotkey: changes.append(
            (topmost, width, message_format, hotkey)
        )
    )
    page.input_width_edit.setValue(620)
    assert changes[-1] == (
        settings.ui.input_topmost,
        620,
        "original_then_translation",
        "Ctrl+Alt+I",
    )
    assert not page.has_unsaved_changes
    assert not hasattr(page, "save_bar")
    assert not hasattr(page, "_show_button")

    settings_page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(settings_page)
    assert not hasattr(settings_page.osc_page, "input_width_spin")
    assert not hasattr(settings_page.ocr_page, "overlay_opacity_spin")
    assert not hasattr(
        settings_page.translation_page.routes_tab,
        "format_combo",
    )


def test_settings_save_preserves_message_format_edited_on_quick_input(qtbot) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    page.load_settings(settings, "memory://config")

    settings.translation.self_route.message_format = "translation_then_original"
    page.collect_settings(settings)

    assert (
        settings.translation.self_route.message_format
        == "translation_then_original"
    )


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


def test_profile_management_separates_machine_translation_and_large_models(
    qtbot,
    tmp_path,
) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles.extend(
        [
            TranslationProfile(
                id="tencent",
                name="Tencent",
                provider="tencent",
            ),
            TranslationProfile(
                id="qwen",
                name="Qwen Plus",
                provider="openai_compatible",
                base_url="https://dashscope.aliyuncs.com/compatible-mode/v1",
                model="qwen-plus",
                options={"model_vendor": "qwen"},
            ),
        ]
    )

    page.load_settings(settings, str(tmp_path / "data" / "config.json"))
    editor = page.translation_page.profile_editor

    assert not hasattr(editor, "profile_tree")
    assert not editor.findChildren(QTreeWidget)
    assert len(editor.findChildren(QScrollArea)) == 1
    assert [
        label.text()
        for label in editor.findChildren(QLabel, "profileGroupTitle")
    ] == [
        "profile_group.builtin",
        "profile_group.machine",
        "profile_group.model",
    ]
    assert len(editor._profile_rows) == 3
    assert any(
        "model_vendor.qwen" in label.text()
        for label in editor.findChildren(QLabel, "profileRowService")
    )
    assert not editor.provider_combo.isEnabled()
    assert editor.profile_combo.isHidden()
    assert editor.profile_name_edit.objectName() != "profileManagementTree"


def test_add_profile_dialog_has_distinct_protocol_families(qtbot) -> None:
    dialog = AddProfileDialog(_FAKE_I18N)
    qtbot.addWidget(dialog)

    assert dialog.tabs.count() == 3
    assert [dialog.tabs.tabText(index) for index in range(3)] == [
        "profile_add.machine_tab",
        "profile_add.model_tab",
        "profile_add.custom_tab",
    ]
    machine = {
        dialog.machine_provider.itemData(index)
        for index in range(dialog.machine_provider.count())
    }
    vendors = {
        dialog.model_vendor.itemData(index)
        for index in range(dialog.model_vendor.count())
    }
    assert machine == {
        "deepl",
        "google_cloud",
        "google_free",
        "aliyun",
        "tencent",
    }
    assert "openai_compatible" not in machine
    assert {"deepseek", "qwen", "doubao", "minimax", "kimi", "zhipu", "openai"} == vendors
    assert "deepl" not in vendors


def test_google_free_new_profile_has_default_endpoint(qtbot) -> None:
    dialog = AddProfileDialog(_FAKE_I18N)
    qtbot.addWidget(dialog)

    dialog.machine_provider.setCurrentIndex(
        dialog.machine_provider.findData("google_free")
    )

    assert dialog.machine_base_url.text() == (
        "https://translate.googleapis.com/translate_a/single"
    )
    profile = dialog._machine_profile()
    assert profile is not None
    assert profile.base_url == dialog.machine_base_url.text()


def test_tencent_new_and_legacy_profiles_show_the_effective_endpoint(qtbot) -> None:
    dialog = AddProfileDialog(_FAKE_I18N)
    qtbot.addWidget(dialog)
    dialog.machine_provider.setCurrentIndex(
        dialog.machine_provider.findData("tencent")
    )

    assert dialog.machine_base_url.text() == "tmt.tencentcloudapi.com"

    legacy = TranslationProfile(
        id="tencent-legacy",
        name="Tencent legacy",
        provider="tencent",
        base_url="",
        api_key="secret-id",
        model="secret-key",
    )
    edit_dialog = AddProfileDialog(_FAKE_I18N, profile=legacy)
    qtbot.addWidget(edit_dialog)

    assert edit_dialog.machine_base_url.text() == "tmt.tencentcloudapi.com"
    edited = edit_dialog._machine_profile()
    assert edited is not None
    assert edited.base_url == "tmt.tencentcloudapi.com"


def test_deepseek_new_profile_uses_canonical_endpoint_without_version_suffix(
    qtbot,
) -> None:
    dialog = AddProfileDialog(_FAKE_I18N)
    qtbot.addWidget(dialog)
    dialog.model_vendor.setCurrentIndex(
        dialog.model_vendor.findData("deepseek")
    )

    assert dialog.model_base_url.text() == "https://api.deepseek.com"


def test_selecting_a_profile_does_not_mark_settings_as_unsaved(qtbot, tmp_path) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles.append(
        TranslationProfile(
            id="google-free",
            name="Google",
            provider="google_free",
            base_url="https://translate.googleapis.com/translate_a/single",
        )
    )
    page.load_settings(settings, str(tmp_path / "config.json"))

    assert not page.has_unsaved_changes
    page.translation_page.profile_editor._select_from_list("google-free")

    assert not page.has_unsaved_changes


def test_edit_profile_dialog_preserves_identity_and_protocol(qtbot) -> None:
    original = TranslationProfile(
        id="google-free",
        name="Google old",
        provider="google_free",
        base_url="",
        timeout_seconds=12,
        region="ap-test",
        options={"custom": "kept"},
    )
    dialog = AddProfileDialog(_FAKE_I18N, profile=original)
    qtbot.addWidget(dialog)

    assert dialog.windowTitle() == "profile_edit.title"
    assert dialog.tabs.tabBar().isHidden()
    assert not dialog.machine_provider.isEnabled()
    assert dialog.machine_base_url.text() == (
        "https://translate.googleapis.com/translate_a/single"
    )
    dialog.machine_name.setText("Google edited")
    dialog.timeout_edit.setValue(20)

    edited = dialog._machine_profile()
    assert edited is not None
    assert edited.id == original.id
    assert edited.name == "Google edited"
    assert edited.provider == original.provider
    assert edited.timeout_seconds == 20
    assert edited.region == original.region
    assert edited.options == original.options


def test_profile_row_edit_is_applied_from_modal_dialog(
    qtbot,
    tmp_path,
    monkeypatch,
) -> None:
    page = SettingsPage(_FAKE_I18N)
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.translation.profiles.append(
        TranslationProfile(
            id="google-edit",
            name="Before",
            provider="google_free",
        )
    )
    page.load_settings(settings, str(tmp_path / "config.json"))
    editor = page.translation_page.profile_editor
    opened: list[str] = []

    def accept_edit(dialog: AddProfileDialog) -> QDialog.DialogCode:
        opened.append(dialog.windowTitle())
        dialog.machine_name.setText("After")
        dialog._profile = dialog._machine_profile()
        return QDialog.DialogCode.Accepted

    monkeypatch.setattr(AddProfileDialog, "exec", accept_edit)

    editor._edit_from_list("google-edit")

    assert opened == ["profile_edit.title"]
    assert editor._profile("google-edit").name == "After"
    assert not editor.profile_name_edit.isVisible()


def test_add_dialog_creates_tencent_and_qwen_profiles_with_different_fields(
    qtbot,
) -> None:
    dialog = AddProfileDialog(_FAKE_I18N)
    qtbot.addWidget(dialog)
    dialog.machine_provider.setCurrentIndex(
        dialog.machine_provider.findData("tencent")
    )
    dialog.machine_key.setText("secret-id")
    dialog.machine_secret.setText("secret-key")

    machine = dialog._machine_profile()

    assert machine is not None
    assert machine.provider == "tencent"
    assert machine.api_key == "secret-id"
    assert machine.model == "secret-key"
    assert dialog._machine_key_label.text() == "profile.secret_id"
    assert not dialog.machine_secret.isHidden()

    dialog.model_vendor.setCurrentIndex(dialog.model_vendor.findData("qwen"))
    dialog.model_id.setText("qwen-plus")
    dialog.model_key.setText("test-key")
    model = dialog._model_profile()

    assert model is not None
    assert model.provider == "openai_compatible"
    assert model.options["model_vendor"] == "qwen"
    assert model.base_url == "https://dashscope.aliyuncs.com/compatible-mode/v1"


def test_add_dialog_configures_aliyun_region_endpoint_and_api(qtbot) -> None:
    dialog = AddProfileDialog(_FAKE_I18N)
    qtbot.addWidget(dialog)
    dialog.machine_provider.setCurrentIndex(
        dialog.machine_provider.findData("aliyun")
    )

    assert not dialog.machine_region.isHidden()
    assert not dialog.machine_api.isHidden()
    assert dialog.machine_region.currentData() is None
    assert dialog.machine_base_url.text() == ""
    assert dialog.machine_region.lineEdit().placeholderText() == (
        "profile.aliyun_region_select"
    )
    dialog.show()
    qtbot.waitExposed(dialog)
    qtbot.mouseClick(
        dialog.machine_region.lineEdit(),
        Qt.MouseButton.LeftButton,
    )
    assert dialog.machine_region.lineEdit().placeholderText() == ""
    assert dialog._machine_key_label.text() == "profile.aliyun_access_key_id"
    assert dialog._machine_secret_label.text() == (
        "profile.aliyun_access_key_secret"
    )

    dialog.machine_region.setCurrentIndex(
        dialog.machine_region.findData("cn-hangzhou")
    )
    assert dialog.machine_base_url.text() == "mt.cn-hangzhou.aliyuncs.com"

    dialog.machine_region.setCurrentIndex(
        dialog.machine_region.findData("ap-southeast-1")
    )
    assert dialog.machine_base_url.text() == (
        "mt.ap-southeast-1.aliyuncs.com"
    )
    dialog.machine_key.setText("test-id")
    dialog.machine_secret.setText("test-secret")
    dialog.machine_api.setCurrentIndex(
        dialog.machine_api.findData("professional")
    )

    profile = dialog._machine_profile()

    assert profile is not None
    assert profile.provider == "aliyun"
    assert profile.region == "ap-southeast-1"
    assert profile.base_url == "mt.ap-southeast-1.aliyuncs.com"
    assert profile.api_key == "test-id"
    assert profile.model == "test-secret"
    assert profile.options["aliyun_api"] == "professional"

    dialog.machine_region.setCurrentText("future-region-1")
    assert dialog.machine_base_url.text() == ""
