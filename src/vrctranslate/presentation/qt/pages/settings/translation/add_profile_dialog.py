from __future__ import annotations

from copy import deepcopy
from uuid import uuid4

from PySide6.QtCore import QEvent, Qt
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QFormLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import (
    MIN_PROFILE_TIMEOUT_SECONDS,
    TranslationProfile,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit

from .constants import (
    ALIYUN_REGIONS,
    MACHINE_TRANSLATION_DEFAULT_ENDPOINTS,
    MACHINE_TRANSLATION_PROVIDERS,
    MODEL_VENDORS,
    aliyun_endpoint_for_region,
    model_vendor_from_profile,
)


class AddProfileDialog(QDialog):
    """Create a profile through an explicit protocol family."""

    def __init__(
        self,
        i18n: I18nManager,
        parent: QWidget | None = None,
        *,
        profile: TranslationProfile | None = None,
    ) -> None:
        super().__init__(parent)
        self._i18n = i18n
        self._editing_profile = deepcopy(profile)
        self._profile: TranslationProfile | None = None
        self.setObjectName("addProfileDialog")
        self.setModal(True)
        self.setMinimumWidth(560)
        self._build_ui()
        self._retranslate()
        if self._editing_profile is not None:
            self._load_profile(self._editing_profile)

    @property
    def profile(self) -> TranslationProfile | None:
        return self._profile

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(22, 20, 22, 18)
        root.setSpacing(14)
        self._title = QLabel()
        self._title.setObjectName("dialogTitle")
        root.addWidget(self._title)
        self.tabs = QTabWidget()
        self.tabs.setObjectName("profileTypeTabs")
        self.machine_tab = QWidget()
        self.model_tab = QWidget()
        self.custom_tab = QWidget()
        self.tabs.addTab(self.machine_tab, "")
        self.tabs.addTab(self.model_tab, "")
        self.tabs.addTab(self.custom_tab, "")
        root.addWidget(self.tabs)

        common_fields = QWidget()
        common_form = QFormLayout(common_fields)
        common_form.setContentsMargins(12, 0, 12, 0)
        self.timeout_edit = NumericLineEdit(
            MIN_PROFILE_TIMEOUT_SECONDS,
            120.0,
            1,
        )
        self.timeout_edit.setValue(MIN_PROFILE_TIMEOUT_SECONDS)
        self._timeout_label = QLabel()
        common_form.addRow(self._timeout_label, self.timeout_edit)
        root.addWidget(common_fields)

        machine_form = QFormLayout(self.machine_tab)
        machine_form.setContentsMargins(12, 16, 12, 12)
        machine_form.setVerticalSpacing(12)
        self.machine_provider = NoWheelComboBox()
        self.machine_name = QLineEdit()
        self.machine_key = QLineEdit()
        self.machine_key.setEchoMode(QLineEdit.EchoMode.Password)
        self.machine_secret = QLineEdit()
        self.machine_secret.setEchoMode(QLineEdit.EchoMode.Password)
        self.machine_region = NoWheelComboBox()
        self.machine_region.setEditable(True)
        self.machine_region.lineEdit().installEventFilter(self)
        self._aliyun_region_placeholder = ""
        self.machine_api = NoWheelComboBox()
        self.machine_base_url = QLineEdit()
        self._machine_provider_label = QLabel()
        self._machine_name_label = QLabel()
        self._machine_key_label = QLabel()
        self._machine_secret_label = QLabel()
        self._machine_region_label = QLabel()
        self._machine_api_label = QLabel()
        self._machine_url_label = QLabel()
        machine_form.addRow(self._machine_provider_label, self.machine_provider)
        machine_form.addRow(self._machine_name_label, self.machine_name)
        machine_form.addRow(self._machine_region_label, self.machine_region)
        machine_form.addRow(self._machine_api_label, self.machine_api)
        machine_form.addRow(self._machine_url_label, self.machine_base_url)
        machine_form.addRow(self._machine_key_label, self.machine_key)
        machine_form.addRow(self._machine_secret_label, self.machine_secret)

        model_form = QFormLayout(self.model_tab)
        model_form.setContentsMargins(12, 16, 12, 12)
        model_form.setVerticalSpacing(12)
        self.model_vendor = NoWheelComboBox()
        self.model_capability = NoWheelComboBox()
        self.model_name = QLineEdit()
        self.model_id = QLineEdit()
        self.model_key = QLineEdit()
        self.model_key.setEchoMode(QLineEdit.EchoMode.Password)
        self.model_base_url = QLineEdit()
        self._model_vendor_label = QLabel()
        self._model_capability_label = QLabel()
        self._model_name_label = QLabel()
        self._model_id_label = QLabel()
        self._model_key_label = QLabel()
        self._model_url_label = QLabel()
        model_form.addRow(self._model_vendor_label, self.model_vendor)
        model_form.addRow(self._model_capability_label, self.model_capability)
        model_form.addRow(self._model_name_label, self.model_name)
        model_form.addRow(self._model_id_label, self.model_id)
        model_form.addRow(self._model_key_label, self.model_key)
        model_form.addRow(self._model_url_label, self.model_base_url)

        custom_form = QFormLayout(self.custom_tab)
        custom_form.setContentsMargins(12, 16, 12, 12)
        custom_form.setVerticalSpacing(12)
        self.custom_capability = NoWheelComboBox()
        self.custom_name = QLineEdit()
        self.custom_model = QLineEdit()
        self.custom_key = QLineEdit()
        self.custom_key.setEchoMode(QLineEdit.EchoMode.Password)
        self.custom_base_url = QLineEdit()
        self._custom_capability_label = QLabel()
        self._custom_name_label = QLabel()
        self._custom_model_label = QLabel()
        self._custom_key_label = QLabel()
        self._custom_url_label = QLabel()
        custom_form.addRow(self._custom_capability_label, self.custom_capability)
        custom_form.addRow(self._custom_name_label, self.custom_name)
        custom_form.addRow(self._custom_model_label, self.custom_model)
        custom_form.addRow(self._custom_key_label, self.custom_key)
        custom_form.addRow(self._custom_url_label, self.custom_base_url)

        self.buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Save
            | QDialogButtonBox.StandardButton.Cancel
        )
        self.buttons.accepted.connect(self._accept_profile)
        self.buttons.rejected.connect(self.reject)
        root.addWidget(self.buttons)

        self.machine_provider.currentIndexChanged.connect(
            self._machine_provider_changed
        )
        self.machine_region.currentTextChanged.connect(
            self._aliyun_region_changed
        )
        self.model_vendor.currentIndexChanged.connect(self._model_vendor_changed)

    def _retranslate(self) -> None:
        t = self._i18n.tr
        title_key = "profile_edit.title" if self._editing_profile else "profile_add.title"
        self.setWindowTitle(t(title_key))
        self._title.setText(t(title_key))
        self.tabs.setTabText(0, t("profile_add.machine_tab"))
        self.tabs.setTabText(1, t("profile_add.model_tab"))
        self.tabs.setTabText(2, t("profile_add.custom_tab"))
        self._machine_provider_label.setText(t("profile_add.machine_provider"))
        self._machine_name_label.setText(t("profile.name"))
        self._machine_region_label.setText(t("profile.aliyun_region"))
        self._machine_api_label.setText(t("profile.aliyun_api"))
        self._machine_url_label.setText(t("profile.interface"))
        self._model_vendor_label.setText(t("profile_add.model_vendor"))
        self._model_capability_label.setText(t("profile_add.capability"))
        self._model_name_label.setText(t("profile.name"))
        self._model_id_label.setText(t("profile_add.model_or_endpoint"))
        self._model_key_label.setText(t("profile.api_key"))
        self._model_url_label.setText(t("profile.interface"))
        self._custom_capability_label.setText(t("profile_add.capability"))
        self._custom_name_label.setText(t("profile.name"))
        self._custom_model_label.setText(t("profile_add.model_or_endpoint"))
        self._custom_key_label.setText(t("profile.api_key"))
        self._custom_url_label.setText(t("profile.interface"))
        self._timeout_label.setText(t("profile.timeout"))
        self._rebuild_catalogs()
        save = self.buttons.button(QDialogButtonBox.StandardButton.Save)
        cancel = self.buttons.button(QDialogButtonBox.StandardButton.Cancel)
        if save is not None:
            save.setText(
                t("profile_add.save" if self._editing_profile else "profile_add.create")
            )
            save.setObjectName("primaryButton")
        if cancel is not None:
            cancel.setText(t("profile_add.cancel"))

    def _rebuild_catalogs(self) -> None:
        self.machine_provider.clear()
        for key, value in MACHINE_TRANSLATION_PROVIDERS:
            self.machine_provider.addItem(self._i18n.tr(key), value)
        self.machine_region.clear()
        for key, value in ALIYUN_REGIONS:
            self.machine_region.addItem(self._i18n.tr(key), value)
        self.machine_region.setCurrentIndex(-1)
        self._aliyun_region_placeholder = self._i18n.tr(
            "profile.aliyun_region_select"
        )
        self.machine_region.lineEdit().setPlaceholderText(
            ""
            if self.machine_region.lineEdit().hasFocus()
            else self._aliyun_region_placeholder
        )
        self.machine_api.clear()
        self.machine_api.addItem(
            self._i18n.tr("profile.aliyun_api_general"),
            "general",
        )
        self.machine_api.addItem(
            self._i18n.tr("profile.aliyun_api_professional"),
            "professional",
        )
        self.model_vendor.clear()
        for key, value, base_url in MODEL_VENDORS:
            self.model_vendor.addItem(self._i18n.tr(key), value)
            self.model_vendor.setItemData(
                self.model_vendor.count() - 1,
                base_url,
                Qt.ItemDataRole.UserRole + 1,
            )
        for combo in (self.model_capability, self.custom_capability):
            combo.clear()
            combo.addItem(
                self._i18n.tr("profile_add.text_model"), "openai_compatible"
            )
            combo.addItem(
                self._i18n.tr("profile_add.visual_model"), "multimodal_openai"
            )
        self._machine_provider_changed()
        self._model_vendor_changed()

    def _machine_provider_changed(self) -> None:
        provider = str(self.machine_provider.currentData() or "deepl")
        previous_provider = str(
            self.machine_provider.property("previousMachineProvider") or ""
        )
        self.machine_provider.setProperty("previousMachineProvider", provider)
        display = self.machine_provider.currentText()
        if not self.machine_name.text().strip():
            self.machine_name.setPlaceholderText(display)
        is_tencent = provider == "tencent"
        is_aliyun = provider == "aliyun"
        uses_secret_pair = is_tencent or is_aliyun
        needs_key = provider not in {"google_free", "test"}
        show_url = provider != "test"
        self._machine_key_label.setText(
            self._i18n.tr(
                "profile.secret_id"
                if is_tencent
                else "profile.aliyun_access_key_id"
                if is_aliyun
                else "profile.api_key"
            )
        )
        self._machine_secret_label.setText(
            self._i18n.tr(
                "profile.aliyun_access_key_secret"
                if is_aliyun
                else "profile.secret_key"
            )
        )
        self.machine_key.setVisible(needs_key)
        self._machine_key_label.setVisible(needs_key)
        self.machine_secret.setVisible(uses_secret_pair)
        self._machine_secret_label.setVisible(uses_secret_pair)
        self.machine_region.setVisible(is_aliyun)
        self._machine_region_label.setVisible(is_aliyun)
        self.machine_api.setVisible(is_aliyun)
        self._machine_api_label.setVisible(is_aliyun)
        self.machine_base_url.setVisible(show_url)
        self._machine_url_label.setVisible(show_url)
        self._machine_url_label.setText(
            self._i18n.tr(
                "profile.aliyun_endpoint" if is_aliyun else "profile.interface"
            )
        )
        default_endpoint = MACHINE_TRANSLATION_DEFAULT_ENDPOINTS.get(provider, "")
        previous_default = str(
            self.machine_base_url.property("machineDefaultEndpoint") or ""
        )
        if default_endpoint:
            current = self.machine_base_url.text().strip()
            automatic_aliyun = str(
                self.machine_base_url.property("aliyunAutoEndpoint") or ""
            )
            known_defaults = {
                *MACHINE_TRANSLATION_DEFAULT_ENDPOINTS.values(),
                previous_default,
                automatic_aliyun,
            }
            if (
                previous_provider != provider
                or not current
                or current in known_defaults
            ):
                self.machine_base_url.setText(default_endpoint)
            self.machine_base_url.setProperty(
                "machineDefaultEndpoint", default_endpoint
            )
            self.machine_base_url.setProperty("aliyunAutoEndpoint", "")
        elif is_aliyun:
            if previous_provider != "aliyun":
                self.machine_base_url.clear()
                self.machine_base_url.setProperty("aliyunAutoEndpoint", "")
            self.machine_base_url.setProperty("machineDefaultEndpoint", "")
            self._aliyun_region_changed()
        else:
            automatic_aliyun = str(
                self.machine_base_url.property("aliyunAutoEndpoint") or ""
            )
            if self.machine_base_url.text().strip() in {
                *MACHINE_TRANSLATION_DEFAULT_ENDPOINTS.values(),
                previous_default,
                automatic_aliyun,
            }:
                self.machine_base_url.clear()
            self.machine_base_url.setProperty("machineDefaultEndpoint", "")
            self.machine_base_url.setProperty("aliyunAutoEndpoint", "")
        self.machine_base_url.setPlaceholderText(
            self._i18n.tr(
                "profile.placeholder_aliyun_endpoint"
                if is_aliyun
                else "profile.placeholder_url"
            )
        )

    def _aliyun_region_changed(self) -> None:
        if str(self.machine_provider.currentData() or "") != "aliyun":
            return
        region = self._selected_aliyun_region()
        current = self.machine_base_url.text().strip()
        previous = str(self.machine_base_url.property("aliyunAutoEndpoint") or "")
        endpoint = aliyun_endpoint_for_region(region)
        if not current or current == previous:
            self.machine_base_url.setText(endpoint)
        self.machine_base_url.setProperty("aliyunAutoEndpoint", endpoint)

    def _model_vendor_changed(self) -> None:
        base_url = str(
            self.model_vendor.currentData(Qt.ItemDataRole.UserRole + 1) or ""
        )
        self.model_base_url.setText(base_url)
        if not self.model_name.text().strip():
            self.model_name.setPlaceholderText(self.model_vendor.currentText())

    def _accept_profile(self) -> None:
        tab = self.tabs.currentIndex()
        if tab == 0:
            profile = self._machine_profile()
        elif tab == 1:
            profile = self._model_profile()
        else:
            profile = self._custom_profile()
        if profile is None:
            QMessageBox.warning(
                self,
                self._i18n.tr("profile_add.incomplete_title"),
                self._i18n.tr("profile_add.incomplete"),
            )
            return
        self._profile = profile
        self.accept()

    def _machine_profile(self) -> TranslationProfile | None:
        provider = str(self.machine_provider.currentData() or "")
        name = self.machine_name.text().strip() or self.machine_provider.currentText()
        key = self.machine_key.text().strip()
        secret = self.machine_secret.text().strip()
        if not provider or (provider not in {"google_free", "test"} and not key):
            return None
        if provider in {"tencent", "aliyun"} and not secret:
            return None
        region = self._existing_region()
        options = self._existing_options()
        if provider == "aliyun":
            region = self._selected_aliyun_region()
            if not region:
                return None
            options["aliyun_api"] = str(
                self.machine_api.currentData() or "general"
            )
        return TranslationProfile(
            id=self._profile_id(),
            name=name,
            provider=provider,
            base_url=self.machine_base_url.text().strip(),
            api_key=key,
            model=secret,
            timeout_seconds=float(self.timeout_edit.value()),
            region=region,
            options=options,
        )

    def _model_profile(self) -> TranslationProfile | None:
        vendor = str(self.model_vendor.currentData() or "")
        provider = str(self.model_capability.currentData() or "")
        model = self.model_id.text().strip()
        key = self.model_key.text().strip()
        base_url = self.model_base_url.text().strip()
        if not all((vendor, provider, model, key, base_url)):
            return None
        name = self.model_name.text().strip() or model
        return TranslationProfile(
            id=self._profile_id(),
            name=name,
            provider=provider,
            base_url=base_url,
            api_key=key,
            model=model,
            timeout_seconds=float(self.timeout_edit.value()),
            region=self._existing_region(),
            options=self._model_options(vendor),
        )

    def _custom_profile(self) -> TranslationProfile | None:
        provider = str(self.custom_capability.currentData() or "")
        name = self.custom_name.text().strip()
        model = self.custom_model.text().strip()
        key = self.custom_key.text().strip()
        base_url = self.custom_base_url.text().strip()
        if not all((provider, name, model, key, base_url)):
            return None
        return TranslationProfile(
            id=self._profile_id(),
            name=name,
            provider=provider,
            base_url=base_url,
            api_key=key,
            model=model,
            timeout_seconds=float(self.timeout_edit.value()),
            region=self._existing_region(),
            options=self._model_options("custom"),
        )

    def _load_profile(self, profile: TranslationProfile) -> None:
        self.timeout_edit.setValue(profile.timeout_seconds)
        if profile.provider in {
            "test",
            *(value for _key, value in MACHINE_TRANSLATION_PROVIDERS),
        }:
            if self.machine_provider.findData(profile.provider) < 0:
                self.machine_provider.insertItem(
                    0,
                    self._i18n.tr("provider.test"),
                    "test",
                )
            self.machine_provider.setCurrentIndex(
                self.machine_provider.findData(profile.provider)
            )
            self.machine_name.setText(profile.name)
            self.machine_base_url.setText(
                profile.base_url
                or MACHINE_TRANSLATION_DEFAULT_ENDPOINTS.get(profile.provider, "")
            )
            self.machine_key.setText(profile.api_key)
            self.machine_secret.setText(profile.model)
            if profile.provider == "aliyun":
                region = profile.region
                index = self.machine_region.findData(region)
                if index >= 0:
                    self.machine_region.setCurrentIndex(index)
                else:
                    self.machine_region.setCurrentText(region)
                api_mode = str(profile.options.get("aliyun_api", "general"))
                self.machine_api.setCurrentIndex(
                    max(0, self.machine_api.findData(api_mode))
                )
                self.machine_base_url.setText(
                    profile.base_url or aliyun_endpoint_for_region(region)
                )
            self.machine_provider.setEnabled(False)
            tab = 0
        else:
            vendor = str(profile.options.get("model_vendor", "")) or (
                model_vendor_from_profile(profile.base_url, profile.name)
            )
            if vendor != "custom" and self.model_vendor.findData(vendor) >= 0:
                self.model_vendor.setCurrentIndex(self.model_vendor.findData(vendor))
                self.model_capability.setCurrentIndex(
                    self.model_capability.findData(profile.provider)
                )
                self.model_name.setText(profile.name)
                self.model_id.setText(profile.model)
                self.model_key.setText(profile.api_key)
                self.model_base_url.setText(profile.base_url)
                self.model_vendor.setEnabled(False)
                self.model_capability.setEnabled(False)
                tab = 1
            else:
                self.custom_capability.setCurrentIndex(
                    self.custom_capability.findData(profile.provider)
                )
                self.custom_name.setText(profile.name)
                self.custom_model.setText(profile.model)
                self.custom_key.setText(profile.api_key)
                self.custom_base_url.setText(profile.base_url)
                self.custom_capability.setEnabled(False)
                tab = 2
        self.tabs.setCurrentIndex(tab)
        self.tabs.tabBar().hide()

    def _profile_id(self) -> str:
        if self._editing_profile is not None:
            return self._editing_profile.id
        return f"profile-{uuid4().hex[:8]}"

    def _selected_aliyun_region(self) -> str:
        index = self.machine_region.currentIndex()
        text = self.machine_region.currentText().strip()
        if index >= 0 and text == self.machine_region.itemText(index):
            return str(self.machine_region.itemData(index) or "").strip()
        return text

    def eventFilter(self, watched, event) -> bool:  # type: ignore[no-untyped-def]
        region_edit = self.machine_region.lineEdit()
        if watched is region_edit:
            if event.type() in {
                QEvent.Type.FocusIn,
                QEvent.Type.MouseButtonPress,
            }:
                region_edit.setPlaceholderText("")
            elif (
                event.type() == QEvent.Type.FocusOut
                and not region_edit.text().strip()
            ):
                region_edit.setPlaceholderText(self._aliyun_region_placeholder)
        return super().eventFilter(watched, event)

    def _existing_region(self) -> str:
        return self._editing_profile.region if self._editing_profile is not None else ""

    def _existing_options(self) -> dict[str, object]:
        if self._editing_profile is None:
            return {}
        return deepcopy(self._editing_profile.options)

    def _model_options(self, vendor: str) -> dict[str, object]:
        options = self._existing_options()
        options["model_vendor"] = vendor
        return options
