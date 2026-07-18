from __future__ import annotations

from copy import deepcopy
from uuid import uuid4

from PySide6.QtCore import Signal
from PySide6.QtWidgets import (
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import (
    MIN_PROFILE_TIMEOUT_SECONDS,
    TranslationProfile,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.common import card, form_layout, scroll_page
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit

from .constants import PROVIDERS
from .helpers import set_combo


class ProfileEditor(QWidget):
    """Owns translation profiles and their provider-specific form fields."""

    profiles_changed = Signal()
    test_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._profiles: list[TranslationProfile] = []
        self._loaded_profile_id = ""
        self._build_ui()
        self.retranslate()

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        scroll, _, layout = scroll_page()
        root.addWidget(scroll)

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

        form = form_layout()
        self.profile_name_edit = QLineEdit()
        self.provider_combo = NoWheelComboBox()
        self.base_url_edit = QLineEdit()
        self.api_key_edit = QLineEdit()
        self.api_key_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.model_edit = QLineEdit()
        self.timeout_spin = NumericLineEdit(
            MIN_PROFILE_TIMEOUT_SECONDS,
            120.0,
            1,
        )
        self._profile_name_label = QLabel()
        self._provider_label = QLabel()
        self.base_url_label = QLabel()
        self.api_key_label = QLabel()
        self.model_label = QLabel()
        self._profile_timeout_label = QLabel()
        form.addRow(self._profile_name_label, self.profile_name_edit)
        form.addRow(self._provider_label, self.provider_combo)
        form.addRow(self.base_url_label, self.base_url_edit)
        form.addRow(self.api_key_label, self.api_key_edit)
        form.addRow(self.model_label, self.model_edit)
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
        self.test_status.setObjectName("translationTestStatus")
        self.test_status.setWordWrap(True)
        self.test_status.hide()
        profile_layout.addWidget(self.test_button)
        profile_layout.addWidget(self.test_status)
        layout.addWidget(profile_card)
        layout.addStretch()

        self.profile_combo.currentIndexChanged.connect(self._profile_selected)
        self.provider_combo.currentIndexChanged.connect(self._update_provider_fields)
        self.new_profile_button.clicked.connect(self._new_profile)
        self.delete_profile_button.clicked.connect(self._delete_profile)
        self.test_button.clicked.connect(self.test_requested)

    def retranslate(self) -> None:
        self._profile_card_title.setText(self._i18n.tr("translation.card_profiles"))
        self._profile_name_label.setText(self._i18n.tr("profile.name"))
        self._provider_label.setText(self._i18n.tr("profile.provider"))
        self.base_url_label.setText(self._i18n.tr("profile.interface"))
        self._profile_timeout_label.setText(self._i18n.tr("profile.timeout"))
        self.new_profile_button.setText(self._i18n.tr("profile.new"))
        self.delete_profile_button.setText(self._i18n.tr("profile.delete"))
        self.test_button.setText(self._i18n.tr("profile.test_button"))
        self._rebuild_provider_combo()
        self._update_provider_fields()

    def load_profiles(self, profiles: list[TranslationProfile]) -> None:
        self._profiles = deepcopy(profiles)
        self._loaded_profile_id = ""
        self.profile_combo.blockSignals(True)
        self.profile_combo.clear()
        for profile in self._profiles:
            self.profile_combo.addItem(profile.name, profile.id)
        self.profile_combo.blockSignals(False)
        if self.profile_combo.count():
            self.profile_combo.setCurrentIndex(0)
            self._profile_selected()

    def profiles(self) -> list[TranslationProfile]:
        self._commit_profile_editor()
        return deepcopy(self._profiles)

    def selected_profile(self) -> TranslationProfile:
        self._commit_profile_editor()
        profile_id = str(self.profile_combo.currentData())
        return deepcopy(self._profile(profile_id))

    def set_test_status(self, message: str, failed: bool = False) -> None:
        self.test_status.setText(message)
        self.test_status.setProperty("failed", failed)
        self.test_status.setVisible(bool(message.strip()))
        self.test_status.style().unpolish(self.test_status)
        self.test_status.style().polish(self.test_status)

    def _profile(self, profile_id: str) -> TranslationProfile:
        for profile in self._profiles:
            if profile.id == profile_id:
                return profile
        raise KeyError(profile_id)

    def _profile_selected(self) -> None:
        if self._loaded_profile_id:
            self._commit_profile_editor()
            self.profiles_changed.emit()
        profile_id = str(self.profile_combo.currentData() or "")
        if not profile_id:
            return
        try:
            profile = self._profile(profile_id)
        except KeyError:
            return
        self._loaded_profile_id = profile_id
        self.profile_name_edit.setText(profile.name)
        set_combo(self.provider_combo, profile.provider)
        self.base_url_edit.setText(profile.base_url)
        self.api_key_edit.setText(profile.api_key)
        self.model_edit.setText(profile.model)
        self.timeout_spin.setValue(profile.timeout_seconds)
        self._update_provider_fields()

    def _commit_profile_editor(self) -> None:
        if not self._loaded_profile_id:
            return
        try:
            profile = self._profile(self._loaded_profile_id)
        except KeyError:
            return
        profile.name = (
            self.profile_name_edit.text().strip()
            or self._i18n.tr("translation.unnamed_profile")
        )
        profile.provider = str(self.provider_combo.currentData())
        profile.base_url = self.base_url_edit.text().strip()
        profile.api_key = self.api_key_edit.text().strip()
        profile.model = self.model_edit.text().strip()
        profile.timeout_seconds = float(self.timeout_spin.value())
        index = self.profile_combo.findData(profile.id)
        if index >= 0:
            self.profile_combo.setItemText(index, profile.name)

    def _new_profile(self) -> None:
        self._commit_profile_editor()
        profile = TranslationProfile(
            id=f"profile-{uuid4().hex[:8]}",
            name=self._i18n.tr("translation.default_profile_name"),
            provider="deepl",
            timeout_seconds=8.0,
        )
        self._profiles.append(profile)
        self.profile_combo.addItem(profile.name, profile.id)
        self.profile_combo.setCurrentIndex(self.profile_combo.count() - 1)
        self.profiles_changed.emit()

    def _delete_profile(self) -> None:
        if len(self._profiles) <= 1:
            self.set_test_status(self._i18n.tr("profile.min_one"), True)
            return
        profile_id = str(self.profile_combo.currentData())
        self._profiles = [item for item in self._profiles if item.id != profile_id]
        self._loaded_profile_id = ""
        self.profile_combo.removeItem(self.profile_combo.currentIndex())
        self._profile_selected()
        self.profiles_changed.emit()

    def _rebuild_provider_combo(self) -> None:
        current = self.provider_combo.currentData()
        self.provider_combo.blockSignals(True)
        self.provider_combo.clear()
        for key, value in PROVIDERS:
            self.provider_combo.addItem(self._i18n.tr(key), value)
        set_combo(self.provider_combo, str(current or ""))
        self.provider_combo.blockSignals(False)

    def _update_provider_fields(self) -> None:
        provider = str(self.provider_combo.currentData())
        minimum_timeout = MIN_PROFILE_TIMEOUT_SECONDS
        self.timeout_spin.minimum = minimum_timeout
        try:
            current_timeout = float(self.timeout_spin.text())
        except ValueError:
            current_timeout = minimum_timeout
        if current_timeout < minimum_timeout:
            self.timeout_spin.setValue(minimum_timeout)
        show_base_url = provider in {
            "deepl", "google_cloud", "openai_compatible", "multimodal_openai", "tencent", "google_free",
        }
        show_api_key = provider in {
            "deepl", "google_cloud", "openai_compatible", "multimodal_openai", "tencent",
        }
        show_model = provider in {"openai_compatible", "multimodal_openai", "tencent"}
        for widget in (self.base_url_label, self.base_url_edit):
            widget.setVisible(show_base_url)
        for widget in (self.api_key_label, self.api_key_edit):
            widget.setVisible(show_api_key)
        for widget in (self.model_label, self.model_edit):
            widget.setVisible(show_model)
        self.api_key_label.setText(
            self._i18n.tr("profile.secret_id" if provider == "tencent" else "profile.api_key")
        )
        self.model_label.setText(
            self._i18n.tr("profile.secret_key" if provider == "tencent" else "profile.model")
        )
        self._update_provider_placeholders()
        if provider == "google_free" and not self.base_url_edit.text().strip():
            self.base_url_edit.setText(
                "https://translate.googleapis.com/translate_a/single"
            )

    def _update_provider_placeholders(self) -> None:
        provider = str(self.provider_combo.currentData())
        api_keys = {
            "deepl": "profile.placeholder_key",
            "google_cloud": "profile.placeholder_key",
            "openai_compatible": "profile.placeholder_key",
            "multimodal_openai": "profile.placeholder_key",
            "tencent": "profile.placeholder_tencent_id",
        }
        model_keys = {
            "openai_compatible": "profile.placeholder_model",
            "multimodal_openai": "profile.placeholder_model",
            "tencent": "profile.placeholder_tencent_key",
        }
        base_urls = {
            "google_free": "profile.placeholder_google_free",
            "tencent": "profile.placeholder_tencent_url",
        }
        self.api_key_edit.setPlaceholderText(
            self._i18n.tr(api_keys.get(provider, "profile.placeholder_key"))
        )
        self.model_edit.setPlaceholderText(
            self._i18n.tr(model_keys[provider]) if provider in model_keys else ""
        )
        self.base_url_edit.setPlaceholderText(
            self._i18n.tr(base_urls.get(provider, "profile.placeholder_url"))
        )
        self._profile_warning.setText(self._i18n.tr("profile.warning"))
        help_keys = {
            "test": "profile.help_test",
            "deepl": "profile.help_deepl",
            "google_cloud": "profile.help_google_cloud",
            "google_free": "profile.help_google_free",
            "tencent": "profile.help_tencent",
            "openai_compatible": "profile.help_openai",
            "multimodal_openai": "profile.help_multimodal",
        }
        self.profile_help.setText(
            self._i18n.tr(help_keys.get(provider, "profile.help_unknown"))
        )
