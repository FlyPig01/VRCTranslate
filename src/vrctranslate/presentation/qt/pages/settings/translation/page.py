from __future__ import annotations

from copy import deepcopy

from PySide6.QtCore import Signal
from PySide6.QtWidgets import QTabWidget, QVBoxLayout, QWidget

from vrctranslate.application.dto import (
    AppSettings,
    SpeechRecognitionProfile,
    TranslationProfile,
)
from vrctranslate.presentation.qt.i18n import I18nManager

from .profile_editor import ProfileEditor
from .glossary_tab import GlossaryTab
from .routes_tab import RoutesTab


class TranslationSettingsPage(QWidget):
    """Stable facade for translation profiles and independent routes."""

    test_translation_requested = Signal()
    glossary_import_requested = Signal(str)
    glossary_export_requested = Signal(str, object)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._working = AppSettings()
        self.profile_editor = ProfileEditor(i18n)
        self.routes_tab = RoutesTab(i18n)
        self.glossary_tab = GlossaryTab(i18n)
        self._builtin_glossary = ()
        self._user_glossary = ()
        self._build_ui()
        self._expose_compatibility_widgets()
        self._connect_components()
        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        self.tabs = QTabWidget()
        self.tabs.setObjectName("settingsSubTabs")
        self.tabs.addTab(self.profile_editor, "")
        self.tabs.addTab(self.routes_tab, "")
        self.tabs.addTab(self.glossary_tab, "")
        root.addWidget(self.tabs)

    def _connect_components(self) -> None:
        self.profile_editor.profiles_changed.connect(self._sync_profiles)
        self.profile_editor.test_requested.connect(
            self.test_translation_requested.emit
        )
        self.glossary_tab.import_requested.connect(
            self.glossary_import_requested.emit
        )
        self.glossary_tab.export_requested.connect(
            self.glossary_export_requested.emit
        )
        self.glossary_tab.enabled.toggled.connect(
            self.routes_tab.set_glossary_global_enabled
        )

    def _expose_compatibility_widgets(self) -> None:
        """Keep attributes consumed by SettingsPage and older UI tests stable."""
        for name in (
            "profile_combo",
            "new_profile_button",
            "delete_profile_button",
            "profile_name_edit",
            "provider_combo",
            "model_vendor_combo",
            "base_url_edit",
            "api_key_edit",
            "model_edit",
            "timeout_spin",
            "base_url_label",
            "api_key_label",
            "model_label",
            "profile_help",
            "test_button",
            "test_status",
        ):
            setattr(self, name, getattr(self.profile_editor, name))
        for name in (
            "self_profile_combo",
            "self_source_combo",
            "self_target_combo",
            "ocr_profile_combo",
            "ocr_source_combo",
            "ocr_target_combo",
            "format_combo",
            "overflow_combo",
            "self_romaji_combo",
            "ocr_romaji_combo",
            "self_romaji_help",
            "ocr_romaji_help",
            "ocr_route_warning",
            "voice_profile_combo",
            "voice_source_combo",
            "voice_target_combo",
            "voice_glossary_enabled",
        ):
            setattr(self, name, getattr(self.routes_tab, name))

    def _retranslate(self) -> None:
        self.tabs.setTabText(0, self._i18n.tr("settings.subtab.profiles"))
        self.tabs.setTabText(1, self._i18n.tr("settings.subtab.routes"))
        self.tabs.setTabText(2, self._i18n.tr("settings.subtab.glossary"))
        self.profile_editor.retranslate()
        self.routes_tab.retranslate()
        self.glossary_tab.retranslate()

    def _sync_profiles(self) -> None:
        self._working.translation.profiles = self.profile_editor.profiles()
        self._working.translation.ensure_routes()
        self.routes_tab.set_profiles(
            self._working.translation.profiles,
            preserve=True,
        )

    def load_settings(self, settings: AppSettings) -> None:
        self._working = deepcopy(settings)
        self._working.translation.ensure_routes()
        self.profile_editor.load_profiles(self._working.translation.profiles)
        self.routes_tab.load_settings(
            self._working.translation,
            self._working.glossary.enabled,
        )
        self.glossary_tab.load(
            self._working.glossary,
            self._builtin_glossary,
            self._user_glossary,
        )

    def collect_settings(self, settings: AppSettings) -> None:
        self._working.translation.profiles = self.profile_editor.profiles()
        self._working.translation.ensure_routes()
        self.routes_tab.collect_settings(self._working.translation)
        self.glossary_tab.collect_settings(self._working.glossary)
        self._working.translation.ensure_routes()
        settings.translation = deepcopy(self._working.translation)
        settings.glossary = deepcopy(self._working.glossary)

    def load_glossary_entries(self, builtin: tuple, user: tuple) -> None:
        self._builtin_glossary = deepcopy(builtin)
        self._user_glossary = deepcopy(user)
        self.glossary_tab.load(
            self._working.glossary,
            self._builtin_glossary,
            self._user_glossary,
        )

    def user_glossary_entries(self) -> list:
        return self.glossary_tab.user_entries()

    def set_user_glossary_entries(self, entries: tuple) -> None:
        self._user_glossary = deepcopy(entries)
        self.glossary_tab.set_user_entries(entries)

    def selected_profile(self) -> TranslationProfile:
        return self.profile_editor.selected_profile()

    def set_speech_profile(
        self,
        profile: SpeechRecognitionProfile | None,
    ) -> None:
        self.routes_tab.set_speech_profile(profile)

    def set_test_status(self, message: str, failed: bool = False) -> None:
        self.profile_editor.set_test_status(message, failed)
