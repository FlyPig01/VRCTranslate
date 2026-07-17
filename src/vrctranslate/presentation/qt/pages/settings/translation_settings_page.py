from __future__ import annotations

from copy import deepcopy
from uuid import uuid4

from PySide6.QtCore import Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QFormLayout,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QProgressBar,
    QPushButton,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings, TranslationProfile
from vrctranslate.application.ports.local_models import LocalTranslationModel
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.options import formats, languages
from vrctranslate.presentation.qt.pages.settings.common import card, scroll_page
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit


PROVIDERS = [
    ("provider.test", "test"),
    ("provider.deepl", "deepl"),
    ("provider.google_cloud", "google_cloud"),
    ("provider.google_free", "google_free"),
    ("provider.tencent", "tencent"),
    ("provider.argos", "argos"),
    ("provider.openai", "openai_compatible"),
]

OVERFLOW_POLICIES = [
    ("overflow.split", "split"),
    ("overflow.truncate", "truncate"),
    ("overflow.reject", "reject"),
]

ARGOS_LANGUAGE_KEYS = {
    "sq": "argos_lang.sq", "ar": "argos_lang.ar", "az": "argos_lang.az",
    "eu": "argos_lang.eu", "bn": "argos_lang.bn", "bg": "argos_lang.bg",
    "ca": "argos_lang.ca", "zh": "argos_lang.zh", "cs": "argos_lang.cs",
    "da": "argos_lang.da", "nl": "argos_lang.nl", "en": "argos_lang.en",
    "eo": "argos_lang.eo", "et": "argos_lang.et", "fi": "argos_lang.fi",
    "fr": "argos_lang.fr", "gl": "argos_lang.gl", "de": "argos_lang.de",
    "el": "argos_lang.el", "he": "argos_lang.he", "hi": "argos_lang.hi",
    "hu": "argos_lang.hu", "id": "argos_lang.id", "ga": "argos_lang.ga",
    "it": "argos_lang.it", "ja": "argos_lang.ja", "ko": "argos_lang.ko",
    "lv": "argos_lang.lv", "lt": "argos_lang.lt", "ms": "argos_lang.ms",
    "no": "argos_lang.no", "fa": "argos_lang.fa", "pl": "argos_lang.pl",
    "pt": "argos_lang.pt", "ro": "argos_lang.ro", "ru": "argos_lang.ru",
    "sk": "argos_lang.sk", "sl": "argos_lang.sl", "es": "argos_lang.es",
    "sv": "argos_lang.sv", "th": "argos_lang.th", "tr": "argos_lang.tr",
    "uk": "argos_lang.uk", "ur": "argos_lang.ur", "vi": "argos_lang.vi",
}


class TranslationSettingsPage(QWidget):
    argos_catalog_requested = Signal()
    test_translation_requested = Signal()
    argos_refresh_requested = Signal()
    argos_install_requested = Signal(str, str, str)
    argos_pivot_install_requested = Signal(list)
    argos_remove_requested = Signal(str, str)
    open_models_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._working = AppSettings()
        self._loaded_profile_id = ""
        self._available_models: list[LocalTranslationModel] = []
        self._installed_models: list[LocalTranslationModel] = []
        self._catalog_requested = False
        self._preferred_argos_source = ""
        self._preferred_argos_target = ""
        self._argos_filters_initialized = False
        self._build_ui()
        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _retranslate(self) -> None:
        # Tab labels
        self.tabs.setTabText(0, self._i18n.tr("settings.subtab.profiles"))
        self.tabs.setTabText(1, self._i18n.tr("settings.subtab.routes"))
        self.tabs.setTabText(2, self._i18n.tr("settings.subtab.argos"))

        # Profiles tab
        self._profile_card_title.setText(self._i18n.tr("translation.card_profiles"))
        self._profile_name_label.setText(self._i18n.tr("profile.name"))
        self._rebuild_provider_combo()
        self._update_provider_label_texts()
        self._profile_timeout_label.setText(self._i18n.tr("profile.timeout"))
        self.new_profile_button.setText(self._i18n.tr("profile.new"))
        self.delete_profile_button.setText(self._i18n.tr("profile.delete"))
        self.test_button.setText(self._i18n.tr("profile.test_button"))
        self._rebuild_overflow_combo()
        self._rebuild_format_combo()
        self._update_provider_fields()
        self._update_provider_placeholders()

        # Routes tab
        self._self_card_title.setText(self._i18n.tr("route.self_card"))
        self._ocr_card_title.setText(self._i18n.tr("route.ocr_card"))
        self._self_profile_label.setText(self._i18n.tr("route.profile"))
        self._ocr_profile_label.setText(self._i18n.tr("route.profile"))
        self._self_source_label.setText(self._i18n.tr("route.source"))
        self._self_target_label.setText(self._i18n.tr("route.target"))
        self._ocr_source_label.setText(self._i18n.tr("route.source"))
        self._ocr_target_label.setText(self._i18n.tr("route.target"))
        self._format_label.setText(self._i18n.tr("route.format"))
        self._overflow_label.setText(self._i18n.tr("route.overflow"))
        self.self_romaji_check.setText(self._i18n.tr("route.romaji"))
        self.ocr_romaji_check.setText(self._i18n.tr("route.romaji"))

        # Argos tab
        self._argos_card_title.setText(self._i18n.tr("argos.card"))
        self._installed_label.setText(self._i18n.tr("argos.installed"))
        self._rebuild_installed_combo()
        self._rebuild_argos_filters()
        self._argos_source_label.setText(self._i18n.tr("argos.source_filter"))
        self._argos_target_label.setText(self._i18n.tr("argos.target_filter"))
        self._available_label.setText(self._i18n.tr("argos.available"))
        self._rebuild_available_combo()
        self._argos_download_help.setText(self._i18n.tr("argos.download_help"))
        self.argos_refresh_button.setText(self._i18n.tr("argos.refresh"))
        self.argos_install_button.setText(self._i18n.tr("argos.install"))
        self.argos_remove_button.setText(self._i18n.tr("argos.remove"))
        self.open_models_button.setText(self._i18n.tr("argos.open_models"))

        # Refresh route profile combo labels
        self._rebuild_route_profile_combos()
        self._update_ocr_warning()
        self._update_route_pivot_warnings()
        self._update_argos_selection_summary()

    # ------------------------------------------------------------------ UI build

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        self.tabs = QTabWidget()
        self.tabs.setObjectName("settingsSubTabs")
        self.tabs.addTab(self._build_profiles_tab(), "")
        self.tabs.addTab(self._build_routes_tab(), "")
        self.tabs.addTab(self._build_argos_tab(), "")
        self.tabs.currentChanged.connect(self._tab_changed)
        root.addWidget(self.tabs)

    def _tab_changed(self, index: int) -> None:
        if index == 2 and not self._catalog_requested:
            self._catalog_requested = True
            self.argos_catalog_requested.emit()

    def _build_profiles_tab(self) -> QWidget:
        scroll, _, layout = scroll_page()
        profile_card, profile_layout = card("")
        self._profile_card_title = profile_layout.itemAt(0).widget()
        self._profile_card_title.setObjectName("cardTitle")
        row = QHBoxLayout()
        self.profile_combo = NoWheelComboBox()
        self.new_profile_button = QPushButton()
        self.delete_profile_button = QPushButton()
        row.addWidget(self.profile_combo, 1)
        row.addWidget(self.new_profile_button)
        row.addWidget(self.delete_profile_button)
        profile_layout.addLayout(row)
        form = QFormLayout()
        self.profile_name_edit = QLineEdit()
        self.provider_combo = NoWheelComboBox()
        self.base_url_edit = QLineEdit()
        self.api_key_edit = QLineEdit()
        self.api_key_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.model_edit = QLineEdit()
        self.timeout_spin = NumericLineEdit(1.0, 120.0, 1)
        self.base_url_label = QLabel()
        self.api_key_label = QLabel()
        self.model_label = QLabel()
        self._profile_name_label = QLabel()
        form.addRow(self._profile_name_label, self.profile_name_edit)
        self._provider_label = QLabel()
        form.addRow(self._provider_label, self.provider_combo)
        form.addRow(self.base_url_label, self.base_url_edit)
        form.addRow(self.api_key_label, self.api_key_edit)
        form.addRow(self.model_label, self.model_edit)
        self._profile_timeout_label = QLabel()
        form.addRow(self._profile_timeout_label, self.timeout_spin)
        profile_layout.addLayout(form)
        self.profile_help = QLabel()
        self.profile_help.setWordWrap(True)
        self.profile_help.setObjectName("inlineNotice")
        profile_layout.addWidget(self.profile_help)
        self._profile_warning = QLabel()
        self._profile_warning.setWordWrap(True)
        self._profile_warning.setObjectName("warningNotice")
        profile_layout.addWidget(self._profile_warning)
        self.test_button = QPushButton()
        self.test_status = QLabel()
        self.test_status.setWordWrap(True)
        profile_layout.addWidget(self.test_button)
        profile_layout.addWidget(self.test_status)
        layout.addWidget(profile_card)
        layout.addStretch()

        self.profile_combo.currentIndexChanged.connect(self._profile_selected)
        self.provider_combo.currentIndexChanged.connect(self._update_provider_fields)
        self.new_profile_button.clicked.connect(self._new_profile)
        self.delete_profile_button.clicked.connect(self._delete_profile)
        self.test_button.clicked.connect(self.test_translation_requested)
        return scroll

    def _build_routes_tab(self) -> QWidget:
        scroll, _, layout = scroll_page()

        osc_card, osc_layout = card("")
        self._self_card_title = osc_layout.itemAt(0).widget()
        self._self_card_title.setObjectName("cardTitle")
        osc_form = QFormLayout()
        self.self_profile_combo = NoWheelComboBox()
        self.self_source_combo = self._language_combo(True)
        self.self_target_combo = self._language_combo(False)
        self.format_combo = NoWheelComboBox()
        self.overflow_combo = NoWheelComboBox()
        self._self_profile_label = QLabel()
        self._self_source_label = QLabel()
        self._self_target_label = QLabel()
        self._format_label = QLabel()
        self._overflow_label = QLabel()
        osc_form.addRow(self._self_profile_label, self.self_profile_combo)
        osc_form.addRow(self._self_source_label, self.self_source_combo)
        osc_form.addRow(self._self_target_label, self.self_target_combo)
        osc_form.addRow(self._format_label, self.format_combo)
        osc_form.addRow(self._overflow_label, self.overflow_combo)
        self.self_romaji_check = QCheckBox()
        osc_form.addRow("", self.self_romaji_check)
        osc_layout.addLayout(osc_form)
        self.self_route_pivot_warning = QLabel()
        self.self_route_pivot_warning.setWordWrap(True)
        self.self_route_pivot_warning.setObjectName("warningNotice")
        self.self_route_pivot_warning.setVisible(False)
        osc_layout.addWidget(self.self_route_pivot_warning)
        layout.addWidget(osc_card)

        ocr_card, ocr_layout = card("")
        self._ocr_card_title = ocr_layout.itemAt(0).widget()
        self._ocr_card_title.setObjectName("cardTitle")
        ocr_form = QFormLayout()
        self.ocr_profile_combo = NoWheelComboBox()
        self.ocr_source_combo = self._language_combo(True)
        self.ocr_target_combo = self._language_combo(False)
        self._ocr_profile_label = QLabel()
        self._ocr_source_label = QLabel()
        self._ocr_target_label = QLabel()
        ocr_form.addRow(self._ocr_profile_label, self.ocr_profile_combo)
        ocr_form.addRow(self._ocr_source_label, self.ocr_source_combo)
        ocr_form.addRow(self._ocr_target_label, self.ocr_target_combo)
        self.ocr_romaji_check = QCheckBox()
        ocr_form.addRow("", self.ocr_romaji_check)
        ocr_layout.addLayout(ocr_form)
        self.ocr_route_warning = QLabel()
        self.ocr_route_warning.setWordWrap(True)
        self.ocr_route_warning.setObjectName("warningNotice")
        ocr_layout.addWidget(self.ocr_route_warning)
        self.ocr_route_pivot_warning = QLabel()
        self.ocr_route_pivot_warning.setWordWrap(True)
        self.ocr_route_pivot_warning.setObjectName("warningNotice")
        self.ocr_route_pivot_warning.setVisible(False)
        ocr_layout.addWidget(self.ocr_route_pivot_warning)
        layout.addWidget(ocr_card)
        layout.addStretch()
        self.ocr_profile_combo.currentIndexChanged.connect(self._update_ocr_warning)
        for combo in (
            self.self_profile_combo, self.self_source_combo, self.self_target_combo,
            self.ocr_profile_combo, self.ocr_source_combo, self.ocr_target_combo,
        ):
            combo.currentIndexChanged.connect(self._update_route_pivot_warnings)
        return scroll

    def _build_argos_tab(self) -> QWidget:
        scroll, _, layout = scroll_page()
        argos_card, argos_layout = card("")
        self._argos_card_title = argos_layout.itemAt(0).widget()
        self._argos_card_title.setObjectName("cardTitle")
        self.argos_component_label = QLabel()
        self.argos_component_label.setWordWrap(True)
        self.argos_component_label.setObjectName("inlineNotice")
        self.argos_index_label = QLabel()
        self.argos_index_label.setWordWrap(True)
        argos_layout.addWidget(self.argos_component_label)
        argos_layout.addWidget(self.argos_index_label)

        form = QFormLayout()
        self.installed_model_combo = NoWheelComboBox()
        self.available_model_combo = NoWheelComboBox()
        self.argos_source_filter = NoWheelComboBox()
        self.argos_target_filter = NoWheelComboBox()
        self._installed_label = QLabel()
        self._available_label = QLabel()
        form.addRow(self._installed_label, self.installed_model_combo)
        self._argos_source_label = QLabel()
        form.addRow(self._argos_source_label, self.argos_source_filter)
        self._argos_target_label = QLabel()
        form.addRow(self._argos_target_label, self.argos_target_filter)
        form.addRow(self._available_label, self.available_model_combo)
        argos_layout.addLayout(form)
        self._argos_download_help = QLabel()
        self._argos_download_help.setWordWrap(True)
        self._argos_download_help.setObjectName("inlineNotice")
        argos_layout.addWidget(self._argos_download_help)
        self.argos_selection_summary = QLabel()
        self.argos_selection_summary.setWordWrap(True)
        self.argos_selection_summary.setObjectName("statusPill")
        argos_layout.addWidget(self.argos_selection_summary)
        self.argos_pivot_hint = QLabel()
        self.argos_pivot_hint.setWordWrap(True)
        self.argos_pivot_hint.setObjectName("warningNotice")
        self.argos_pivot_hint.setVisible(False)
        argos_layout.addWidget(self.argos_pivot_hint)
        self.argos_pivot_install_button = QPushButton()
        self.argos_pivot_install_button.setVisible(False)
        self.argos_pivot_install_button.clicked.connect(self._request_pivot_install)
        argos_layout.addWidget(self.argos_pivot_install_button)
        self.argos_progress_bar = QProgressBar()
        self.argos_progress_bar.setVisible(False)
        self.argos_progress_bar.setTextVisible(True)
        argos_layout.addWidget(self.argos_progress_bar)
        buttons = QHBoxLayout()
        self.argos_refresh_button = QPushButton()
        self.argos_install_button = QPushButton()
        self.argos_remove_button = QPushButton()
        self.open_models_button = QPushButton()
        self.argos_refresh_button.setIcon(load_icon("ui/action_refresh.svg"))
        self.open_models_button.setIcon(load_icon("ui/action_folder.svg"))
        for button in (
            self.argos_refresh_button,
            self.argos_install_button,
            self.argos_remove_button,
            self.open_models_button,
        ):
            buttons.addWidget(button)
        argos_layout.addLayout(buttons)
        self.argos_status = QLabel()
        self.argos_status.setWordWrap(True)
        argos_layout.addWidget(self.argos_status)
        layout.addWidget(argos_card)
        layout.addStretch()

        self.argos_refresh_button.clicked.connect(self.argos_refresh_requested)
        self.argos_install_button.clicked.connect(self._request_install)
        self.argos_remove_button.clicked.connect(self._request_remove)
        self.open_models_button.clicked.connect(self.open_models_requested)
        self.argos_source_filter.currentIndexChanged.connect(self._filter_models)
        self.argos_target_filter.currentIndexChanged.connect(self._filter_models)
        self.available_model_combo.currentIndexChanged.connect(
            self._update_argos_selection_summary
        )
        return scroll

    def _language_combo(self, include_auto: bool) -> NoWheelComboBox:
        combo = NoWheelComboBox()
        for label, value in languages(self._i18n):
            if include_auto or value != "auto":
                combo.addItem(label, value)
        return combo

    # ------------------------------------------------------------------ combo rebuild helpers

    def _rebuild_provider_combo(self) -> None:
        current = self.provider_combo.currentData()
        self.provider_combo.blockSignals(True)
        self.provider_combo.clear()
        for key, value in PROVIDERS:
            self.provider_combo.addItem(self._i18n.tr(key), value)
        idx = self.provider_combo.findData(current)
        if idx >= 0:
            self.provider_combo.setCurrentIndex(idx)
        self.provider_combo.blockSignals(False)

    def _rebuild_overflow_combo(self) -> None:
        current = self.overflow_combo.currentData()
        self.overflow_combo.clear()
        for key, value in OVERFLOW_POLICIES:
            self.overflow_combo.addItem(self._i18n.tr(key), value)
        idx = self.overflow_combo.findData(current)
        if idx >= 0:
            self.overflow_combo.setCurrentIndex(idx)

    def _rebuild_format_combo(self) -> None:
        current = self.format_combo.currentData()
        self.format_combo.clear()
        for label, value in formats(self._i18n):
            self.format_combo.addItem(label, value)
        idx = self.format_combo.findData(current)
        if idx >= 0:
            self.format_combo.setCurrentIndex(idx)

    def _rebuild_route_profile_combos(self) -> None:
        for combo in (self.self_profile_combo, self.ocr_profile_combo):
            current = combo.currentData()
            combo.blockSignals(True)
            for i in range(combo.count()):
                profile_id = combo.itemData(i)
                if profile_id:
                    try:
                        profile = self._working.translation.profile(str(profile_id))
                        combo.setItemText(i, profile.name)
                    except KeyError:
                        pass
            combo.blockSignals(False)

    def _rebuild_argos_filters(self) -> None:
        source_current = self.argos_source_filter.currentData()
        target_current = self.argos_target_filter.currentData()
        sources = sorted({item.source_language for item in self._available_models})
        targets = sorted({item.target_language for item in self._available_models})
        selected_source = str(source_current or "")
        selected_target = str(target_current or "")
        if not self._argos_filters_initialized:
            selected_source = (
                self._preferred_argos_source
                if self._preferred_argos_source in sources
                else ""
            )
            selected_target = (
                self._preferred_argos_target
                if self._preferred_argos_target in targets
                else ""
            )
        for combo, any_key, values, selected in (
            (self.argos_source_filter, "argos.source_any", sources, selected_source),
            (self.argos_target_filter, "argos.target_any", targets, selected_target),
        ):
            combo.blockSignals(True)
            combo.clear()
            combo.addItem(self._i18n.tr(any_key), "")
            for value in values:
                combo.addItem(self._language_label(value), value)
            self._set_combo(combo, selected)
            combo.blockSignals(False)
        self._argos_filters_initialized = True

    def _rebuild_installed_combo(self) -> None:
        current = self.installed_model_combo.currentData()
        self.installed_model_combo.clear()
        for model in self._installed_models:
            self.installed_model_combo.addItem(self._model_label(model), model)
        if not self._installed_models:
            self.installed_model_combo.addItem(self._i18n.tr("argos.none_installed"), None)
        idx = self.installed_model_combo.findData(current)
        if idx >= 0:
            self.installed_model_combo.setCurrentIndex(idx)

    def _rebuild_available_combo(self) -> None:
        current = self.available_model_combo.currentData()
        self.available_model_combo.clear()
        source = str(self.argos_source_filter.currentData() or "")
        target = str(self.argos_target_filter.currentData() or "")
        for model in self._available_models:
            if source and model.source_language != source:
                continue
            if target and model.target_language != target:
                continue
            self.available_model_combo.addItem(self._model_label(model), model)
        if not self.available_model_combo.count():
            self.available_model_combo.addItem(self._i18n.tr("argos.no_match"), None)
        idx = self.available_model_combo.findData(current)
        if idx >= 0:
            self.available_model_combo.setCurrentIndex(idx)

    def _update_provider_label_texts(self) -> None:
        self._provider_label.setText(self._i18n.tr("profile.provider"))
        self.base_url_label.setText(self._i18n.tr("profile.interface"))

    def _update_provider_placeholders(self) -> None:
        provider = str(self.provider_combo.currentData())
        api_key_placeholders = {
            "deepl": "profile.placeholder_key",
            "google_cloud": "profile.placeholder_key",
            "openai_compatible": "profile.placeholder_key",
            "tencent": "profile.placeholder_tencent_id",
        }
        model_placeholders = {
            "openai_compatible": "profile.placeholder_model",
            "tencent": "profile.placeholder_tencent_key",
        }
        base_url_placeholders = {
            "google_free": "profile.placeholder_google_free",
            "tencent": "profile.placeholder_tencent_url",
        }
        self.api_key_edit.setPlaceholderText(
            self._i18n.tr(api_key_placeholders.get(provider, "profile.placeholder_key"))
        )
        self.model_edit.setPlaceholderText(
            self._i18n.tr(model_placeholders.get(provider, "")) if provider in model_placeholders else ""
        )
        self.base_url_edit.setPlaceholderText(
            self._i18n.tr(base_url_placeholders.get(provider, "profile.placeholder_url"))
        )
        self._profile_warning.setText(self._i18n.tr("profile.warning"))
        help_keys = {
            "test": "profile.help_test",
            "deepl": "profile.help_deepl",
            "google_cloud": "profile.help_google_cloud",
            "google_free": "profile.help_google_free",
            "tencent": "profile.help_tencent",
            "argos": "profile.help_argos",
            "openai_compatible": "profile.help_openai",
        }
        self.profile_help.setText(
            self._i18n.tr(help_keys.get(provider, "profile.help_unknown"))
        )

    # ------------------------------------------------------------------ settings load / collect

    def load_settings(
        self,
        settings: AppSettings,
        argos_available: bool,
        model_directory: str,
    ) -> None:
        self._working = deepcopy(settings)
        self._preferred_argos_source = self._argos_code(
            settings.translation.ocr_route.source_language
        )
        self._preferred_argos_target = self._argos_code(
            settings.translation.ocr_route.target_language
        )
        self._argos_filters_initialized = False
        self._working.translation.ensure_routes()
        self.profile_combo.clear()
        for profile in self._working.translation.profiles:
            self.profile_combo.addItem(profile.name, profile.id)
        self._populate_route_profiles()
        route = settings.translation
        self._set_combo(self.self_profile_combo, route.self_route.profile_id)
        self._set_combo(self.ocr_profile_combo, route.ocr_route.profile_id)
        self._set_combo(self.self_source_combo, route.self_route.source_language)
        self._set_combo(self.self_target_combo, route.self_route.target_language)
        self._set_combo(self.ocr_source_combo, route.ocr_route.source_language)
        self._set_combo(self.ocr_target_combo, route.ocr_route.target_language)
        self._set_combo(self.format_combo, route.self_route.message_format)
        self._set_combo(self.overflow_combo, route.self_route.overflow_policy)
        self.self_romaji_check.setChecked(route.self_route.romaji_to_kana)
        self.ocr_romaji_check.setChecked(route.ocr_route.romaji_to_kana)
        self._loaded_profile_id = ""
        self.profile_combo.setCurrentIndex(0)
        self._profile_selected()
        if argos_available:
            self.argos_component_label.setText(
                self._i18n.tr("argos.component_ok", path=model_directory)
            )
        else:
            self.argos_component_label.setText(self._i18n.tr("argos.component_missing"))
        self.argos_refresh_button.setEnabled(argos_available)
        self.argos_install_button.setEnabled(False)
        self.argos_remove_button.setEnabled(argos_available)
        self._update_ocr_warning()
        self._update_route_pivot_warnings()

    def collect_settings(self, settings: AppSettings) -> None:
        self._commit_profile_editor()
        settings.translation.profiles = deepcopy(self._working.translation.profiles)
        settings.translation.ensure_routes()
        settings.translation.self_route.profile_id = str(self.self_profile_combo.currentData())
        settings.translation.ocr_route.profile_id = str(self.ocr_profile_combo.currentData())
        settings.translation.self_route.source_language = str(self.self_source_combo.currentData())
        settings.translation.self_route.target_language = str(self.self_target_combo.currentData())
        settings.translation.ocr_route.source_language = str(self.ocr_source_combo.currentData())
        settings.translation.ocr_route.target_language = str(self.ocr_target_combo.currentData())
        settings.translation.self_route.message_format = str(self.format_combo.currentData())
        settings.translation.self_route.overflow_policy = str(self.overflow_combo.currentData())
        settings.translation.self_route.romaji_to_kana = self.self_romaji_check.isChecked()
        settings.translation.ocr_route.romaji_to_kana = self.ocr_romaji_check.isChecked()

    # ------------------------------------------------------------------ profile management

    def selected_profile(self) -> TranslationProfile:
        self._commit_profile_editor()
        profile_id = str(self.profile_combo.currentData())
        return deepcopy(self._working.translation.profile(profile_id))

    def set_test_status(self, message: str, failed: bool = False) -> None:
        self.test_status.setText(message)
        self.test_status.setProperty("failed", failed)

    # ------------------------------------------------------------------ argos model catalog

    def set_model_catalog(
        self,
        state: str,
        installed: list[LocalTranslationModel],
        available: list[LocalTranslationModel],
        disk_usage: int,
        message: str = "",
    ) -> None:
        self._available_models = list(available)
        self._installed_models = list(installed)
        self._rebuild_installed_combo()
        self._rebuild_argos_filters()
        self._filter_models()
        state_keys = {
            "component_missing": "argos.state_component_missing",
            "index_missing": "argos.state_index_missing",
            "loading": "argos.state_loading",
            "error": "argos.state_error",
            "empty": "argos.state_empty",
            "ready": "argos.state_ready",
        }
        state_key = state_keys.get(state, "")
        if state_key:
            if state == "ready":
                state_text = self._i18n.tr(state_key, count=len(available))
            else:
                state_text = self._i18n.tr(state_key)
        else:
            state_text = state
        self.argos_index_label.setText(self._i18n.tr("argos.index", state=state_text))
        size_str = self._format_bytes(disk_usage)
        self.argos_status.setText(
            (message + "\n" if message else "")
            + self._i18n.tr("argos.status_installed", count=len(installed), size=size_str)
        )
        self.argos_install_button.setEnabled(
            state in {"ready", "empty"}
            and isinstance(
                self.available_model_combo.currentData(), LocalTranslationModel
            )
        )
        self.argos_remove_button.setEnabled(bool(installed))
        self._update_route_pivot_warnings()

    def set_argos_status(self, message: str) -> None:
        self.argos_status.setText(message)

    def set_argos_progress(self, downloaded: int, total: int) -> None:
        if total > 0:
            self.argos_progress_bar.setRange(0, total)
            self.argos_progress_bar.setValue(downloaded)
            pct = downloaded * 100 // total if total else 0
            self.argos_progress_bar.setFormat(
                f"{downloaded // 1024} KB / {total // 1024} KB ({pct}%)"
            )
        else:
            self.argos_progress_bar.setRange(0, 0)
            self.argos_progress_bar.setFormat(self._i18n.tr("argos.progress_downloading"))
        self.argos_progress_bar.setVisible(True)

    def hide_argos_progress(self) -> None:
        self.argos_progress_bar.setVisible(False)

    # ------------------------------------------------------------------ profile editor

    def _profile_selected(self) -> None:
        if self._loaded_profile_id:
            self._commit_profile_editor()
        profile_id = str(self.profile_combo.currentData() or "")
        if not profile_id:
            return
        try:
            profile = self._working.translation.profile(profile_id)
        except KeyError:
            return
        self._loaded_profile_id = profile_id
        self.profile_name_edit.setText(profile.name)
        self._set_combo(self.provider_combo, profile.provider)
        self.base_url_edit.setText(profile.base_url)
        self.api_key_edit.setText(profile.api_key)
        self.model_edit.setText(profile.model)
        self.timeout_spin.setValue(profile.timeout_seconds)
        self._update_provider_fields()

    def _commit_profile_editor(self) -> None:
        if not self._loaded_profile_id:
            return
        try:
            profile = self._working.translation.profile(self._loaded_profile_id)
        except KeyError:
            return
        profile.name = self.profile_name_edit.text().strip() or self._i18n.tr("translation.unnamed_profile")
        profile.provider = str(self.provider_combo.currentData())
        profile.base_url = self.base_url_edit.text().strip()
        profile.api_key = self.api_key_edit.text().strip()
        profile.model = self.model_edit.text().strip()
        profile.timeout_seconds = float(self.timeout_spin.value())
        index = self.profile_combo.findData(profile.id)
        if index >= 0:
            self.profile_combo.setItemText(index, profile.name)
        self._populate_route_profiles(preserve=True)

    def _new_profile(self) -> None:
        self._commit_profile_editor()
        profile = TranslationProfile(
            id=f"profile-{uuid4().hex[:8]}",
            name=self._i18n.tr("translation.default_profile_name"),
            provider="deepl",
            timeout_seconds=8.0,
        )
        self._working.translation.profiles.append(profile)
        self.profile_combo.addItem(profile.name, profile.id)
        self.profile_combo.setCurrentIndex(self.profile_combo.count() - 1)
        self._populate_route_profiles(preserve=True)

    def _delete_profile(self) -> None:
        if len(self._working.translation.profiles) <= 1:
            self.set_test_status(self._i18n.tr("profile.min_one"), True)
            return
        profile_id = str(self.profile_combo.currentData())
        self._working.translation.profiles = [
            item for item in self._working.translation.profiles if item.id != profile_id
        ]
        self._loaded_profile_id = ""
        self.profile_combo.removeItem(self.profile_combo.currentIndex())
        self._working.translation.ensure_routes()
        self._populate_route_profiles()
        self._profile_selected()

    def _populate_route_profiles(self, preserve: bool = False) -> None:
        self_id = (
            str(self.self_profile_combo.currentData())
            if preserve
            else self._working.translation.self_route.profile_id
        )
        ocr_id = (
            str(self.ocr_profile_combo.currentData())
            if preserve
            else self._working.translation.ocr_route.profile_id
        )
        for combo, selected in (
            (self.self_profile_combo, self_id),
            (self.ocr_profile_combo, ocr_id),
        ):
            combo.blockSignals(True)
            combo.clear()
            for profile in self._working.translation.profiles:
                combo.addItem(profile.name, profile.id)
            self._set_combo(combo, selected)
            combo.blockSignals(False)
        self._update_ocr_warning()
        self._update_route_pivot_warnings()

    # ------------------------------------------------------------------ provider field visibility

    def _update_provider_fields(self) -> None:
        provider = str(self.provider_combo.currentData())
        show_base_url = provider in {
            "deepl", "google_cloud", "openai_compatible", "tencent", "google_free",
        }
        show_api_key = provider in {
            "deepl", "google_cloud", "openai_compatible", "tencent",
        }
        show_model = provider in {"openai_compatible", "tencent"}
        self.base_url_label.setVisible(show_base_url)
        self.base_url_edit.setVisible(show_base_url)
        self.api_key_label.setVisible(show_api_key)
        self.api_key_edit.setVisible(show_api_key)
        self.model_label.setVisible(show_model)
        self.model_edit.setVisible(show_model)
        api_key_labels = {
            "tencent": "profile.secret_id",
        }
        model_labels = {
            "tencent": "profile.secret_key",
        }
        self.api_key_label.setText(
            self._i18n.tr(api_key_labels.get(provider, "profile.api_key"))
        )
        self.model_label.setText(
            self._i18n.tr(model_labels.get(provider, "profile.model"))
        )
        self._update_provider_placeholders()
        if provider == "google_free" and not self.base_url_edit.text().strip():
            self.base_url_edit.setText("https://translate.googleapis.com/translate_a/single")

    # ------------------------------------------------------------------ route warnings

    def _update_ocr_warning(self) -> None:
        profile_id = str(self.ocr_profile_combo.currentData() or "")
        try:
            provider = self._working.translation.profile(profile_id).provider
        except KeyError:
            provider = ""
        self.ocr_route_warning.setText(
            self._i18n.tr("route.ocr_warning_llm")
            if provider == "openai_compatible"
            else self._i18n.tr("route.ocr_warning_default")
        )

    # ------------------------------------------------------------------ argos filters & models

    def _rebuild_filters(self) -> None:
        self._rebuild_argos_filters()

    def _filter_models(self) -> None:
        self._rebuild_available_combo()
        self._update_argos_selection_summary()
        self._update_pivot_hint()

    def _update_argos_selection_summary(self) -> None:
        model = self.available_model_combo.currentData()
        if not isinstance(model, LocalTranslationModel):
            self.argos_selection_summary.setText(self._i18n.tr("argos.no_selection"))
            self.argos_install_button.setEnabled(False)
            return
        self.argos_selection_summary.setText(
            self._i18n.tr(
                "argos.ready_install",
                source=self._language_label(model.source_language),
                target=self._language_label(model.target_language),
            )
        )
        self.argos_install_button.setEnabled(True)

    def _update_pivot_hint(self) -> None:
        source = str(self.argos_source_filter.currentData() or "")
        target = str(self.argos_target_filter.currentData() or "")
        if not source or not target or source == target:
            self.argos_pivot_hint.setVisible(False)
            self.argos_pivot_install_button.setVisible(False)
            return
        installed_pairs = {(m.source_language, m.target_language) for m in self._installed_models}
        if (source, target) in installed_pairs:
            self.argos_pivot_hint.setVisible(False)
            self.argos_pivot_install_button.setVisible(False)
            return
        from_source = {m.target_language for m in self._installed_models if m.source_language == source}
        to_target = {m.source_language for m in self._installed_models if m.target_language == target}
        existing_pivots = from_source & to_target
        if existing_pivots:
            pivot_lang = next(iter(existing_pivots))
            self.argos_pivot_hint.setText(
                self._i18n.tr(
                    "argos.pivot_installed",
                    pivot=self._language_label(pivot_lang),
                    source=self._language_label(source),
                    target=self._language_label(target),
                )
            )
            self.argos_pivot_hint.setVisible(True)
            self.argos_pivot_install_button.setVisible(False)
            return
        online_from_source = {m.target_language for m in self._available_models if m.source_language == source}
        online_to_target = {m.source_language for m in self._available_models if m.target_language == target}
        online_pivots = online_from_source & online_to_target
        if not online_pivots:
            self.argos_pivot_hint.setText(
                self._i18n.tr(
                    "argos.pivot_no_direct",
                    source=self._language_label(source),
                    target=self._language_label(target),
                )
            )
            self.argos_pivot_hint.setVisible(True)
            self.argos_pivot_install_button.setVisible(False)
            return
        pivot_lang = next(iter(online_pivots))
        missing: list[tuple[str, str]] = []
        if (source, pivot_lang) not in installed_pairs:
            missing.append((source, pivot_lang))
        if (pivot_lang, target) not in installed_pairs:
            missing.append((pivot_lang, target))
        if not missing:
            self.argos_pivot_hint.setVisible(False)
            self.argos_pivot_install_button.setVisible(False)
            return
        missing_desc = " 和 ".join(
            f"{self._language_label(s)} → {self._language_label(t)}" for s, t in missing
        )
        self.argos_pivot_hint.setText(
            self._i18n.tr(
                "argos.pivot_available",
                source=self._language_label(source),
                target=self._language_label(target),
                missing=missing_desc,
                pivot=self._language_label(pivot_lang),
            )
        )
        self.argos_pivot_hint.setVisible(True)
        missing_with_versions: list[tuple[str, str, str]] = []
        for src, tgt in missing:
            version = ""
            for m in self._available_models:
                if m.source_language == src and m.target_language == tgt:
                    version = m.package_version
                    break
            missing_with_versions.append((src, tgt, version))
        self._pivot_missing_models = missing_with_versions
        self.argos_pivot_install_button.setText(
            self._i18n.tr("argos.pivot_install_button", count=len(missing_with_versions))
        )
        self.argos_pivot_install_button.setVisible(True)

    def _request_pivot_install(self) -> None:
        models = getattr(self, "_pivot_missing_models", [])
        if models:
            self.argos_pivot_install_requested.emit(list(models))

    def _update_route_pivot_warnings(self) -> None:
        for profile_combo, source_combo, target_combo, warning_label in (
            (self.self_profile_combo, self.self_source_combo, self.self_target_combo, self.self_route_pivot_warning),
            (self.ocr_profile_combo, self.ocr_source_combo, self.ocr_target_combo, self.ocr_route_pivot_warning),
        ):
            profile_id = str(profile_combo.currentData() or "")
            try:
                provider = self._working.translation.profile(profile_id).provider
            except KeyError:
                provider = ""
            if provider != "argos":
                warning_label.setVisible(False)
                continue
            source = self._argos_code(str(source_combo.currentData() or ""))
            target = self._argos_code(str(target_combo.currentData() or ""))
            if not source or not target or source == target:
                warning_label.setVisible(False)
                continue
            installed_pairs = {(m.source_language, m.target_language) for m in self._installed_models}
            if (source, target) in installed_pairs:
                warning_label.setVisible(False)
                continue
            from_source = {m.target_language for m in self._installed_models if m.source_language == source}
            to_target = {m.source_language for m in self._installed_models if m.target_language == target}
            existing_pivots = from_source & to_target
            if existing_pivots:
                pivot_lang = next(iter(existing_pivots))
                warning_label.setText(
                    self._i18n.tr("route.pivot_installed", pivot=self._language_label(pivot_lang))
                )
            else:
                warning_label.setText(self._i18n.tr("route.pivot_missing"))
            warning_label.setVisible(True)

    def _request_install(self) -> None:
        model = self.available_model_combo.currentData()
        if isinstance(model, LocalTranslationModel):
            self.argos_install_requested.emit(
                model.source_language, model.target_language, model.package_version
            )

    def _request_remove(self) -> None:
        model = self.installed_model_combo.currentData()
        if isinstance(model, LocalTranslationModel):
            self.argos_remove_requested.emit(model.source_language, model.target_language)

    # ------------------------------------------------------------------ helpers

    @staticmethod
    def _argos_code(value: str) -> str:
        normalized = value.strip().lower()
        if normalized == "auto":
            return ""
        return {"zh-cn": "zh", "zh-tw": "zh"}.get(normalized, normalized)

    def _language_label(self, code: str) -> str:
        key = ARGOS_LANGUAGE_KEYS.get(code, "argos_lang.unknown")
        name = self._i18n.tr(key)
        return f"{name}（{code}）"

    def _model_label(self, model: LocalTranslationModel) -> str:
        suffix = f" · 版本 {model.package_version}" if model.package_version else ""
        return (
            f"{self._language_label(model.source_language)} → "
            f"{self._language_label(model.target_language)}{suffix}"
        )

    @staticmethod
    def _set_combo(combo: NoWheelComboBox, value: str) -> None:
        index = combo.findData(value)
        if index >= 0:
            combo.setCurrentIndex(index)

    @staticmethod
    def _format_bytes(value: int) -> str:
        size = float(value)
        for unit in ("B", "KB", "MB", "GB"):
            if size < 1024 or unit == "GB":
                return f"{size:.1f} {unit}"
            size /= 1024
        return f"{value} B"
