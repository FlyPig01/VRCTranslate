from __future__ import annotations

from copy import deepcopy

from PySide6.QtCore import Signal
from PySide6.QtGui import QResizeEvent
from PySide6.QtWidgets import (
    QDialog,
    QFrame,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import (
    MIN_PROFILE_TIMEOUT_SECONDS,
    TranslationProfile,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.common import (
    card,
    form_layout,
    scroll_page,
)
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit

from .add_profile_dialog import AddProfileDialog
from .constants import (
    GOOGLE_FREE_ENDPOINT,
    LARGE_MODEL_PROVIDERS,
    MODEL_VENDORS,
    PROVIDERS,
    TENCENT_TRANSLATION_ENDPOINT,
    model_vendor_from_profile,
)
from .helpers import set_combo


class ProfileEditor(QWidget):
    """Grouped profile management plus the existing provider-aware editor."""

    profiles_changed = Signal()
    structure_changed = Signal()
    test_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._profiles: list[TranslationProfile] = []
        self._loaded_profile_id = ""
        self._profile_rows: dict[str, QFrame] = {}
        self._profile_name_labels: dict[str, QPushButton] = {}
        self._profile_row_parts: dict[
            str,
            tuple[QGridLayout, QPushButton, QLabel, QWidget],
        ] = {}
        self._compact: bool | None = None
        self._syncing_selection = False
        self._build_ui()
        self.retranslate()

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        self.scroll, _, layout = scroll_page()
        root.addWidget(self.scroll)

        management_card, management_layout = card("")
        self._management_title = management_layout.itemAt(0).widget()
        self._management_subtitle = QLabel()
        self._management_subtitle.setObjectName("pageSubtitle")
        self._management_subtitle.setWordWrap(True)
        management_layout.addWidget(self._management_subtitle)
        action_row = QHBoxLayout()
        self.new_profile_button = QPushButton()
        self.new_profile_button.setObjectName("primaryButton")
        action_row.addWidget(self.new_profile_button)
        action_row.addStretch()
        management_layout.addLayout(action_row)
        self.profile_list_header = QFrame()
        self.profile_list_header.setObjectName("profileListHeader")
        header_layout = QGridLayout(self.profile_list_header)
        header_layout.setContentsMargins(14, 0, 10, 0)
        header_layout.setHorizontalSpacing(12)
        self._profile_column = QLabel()
        self._service_column = QLabel()
        self._actions_column = QLabel()
        header_layout.addWidget(self._profile_column, 0, 0)
        header_layout.addWidget(self._service_column, 0, 1)
        header_layout.addWidget(self._actions_column, 0, 2)
        header_layout.setColumnStretch(0, 2)
        header_layout.setColumnStretch(1, 2)
        header_layout.setColumnStretch(2, 0)
        management_layout.addWidget(self.profile_list_header)
        self.profile_list = QWidget()
        self.profile_list.setObjectName("profileManagementList")
        self.profile_list_layout = QVBoxLayout(self.profile_list)
        self.profile_list_layout.setContentsMargins(0, 0, 0, 0)
        self.profile_list_layout.setSpacing(0)
        management_layout.addWidget(self.profile_list)
        layout.addWidget(management_card)

        editor_card, editor_layout = card("")
        self._profile_card_title = editor_layout.itemAt(0).widget()
        self._profile_card_title.setObjectName("cardTitle")

        # Kept as a hidden compatibility selector for routes/tests and as one
        # stable source of current profile state.
        self.profile_combo = NoWheelComboBox(self)
        self.profile_combo.hide()
        self.delete_profile_button = QPushButton(self)
        self.delete_profile_button.hide()

        form = form_layout()
        self.profile_name_edit = QLineEdit()
        self.provider_combo = NoWheelComboBox()
        self.provider_combo.setEnabled(False)
        self.model_vendor_combo = NoWheelComboBox()
        self.model_vendor_combo.setEnabled(False)
        self.base_url_edit = QLineEdit()
        self.api_key_edit = QLineEdit()
        self.api_key_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.model_edit = QLineEdit()
        self.timeout_spin = NumericLineEdit(
            MIN_PROFILE_TIMEOUT_SECONDS,
            120.0,
            1,
        )
        # These controls only remain for compatibility with older callers.
        # The visible editor is the modal AddProfileDialog, so programmatic
        # synchronization here must never make the settings page look edited.
        for compatibility_widget in (
            self.profile_name_edit,
            self.provider_combo,
            self.model_vendor_combo,
            self.base_url_edit,
            self.api_key_edit,
            self.model_edit,
            self.timeout_spin,
        ):
            compatibility_widget.setProperty("skipDirtyTracking", True)
        self._profile_name_label = QLabel()
        self._provider_label = QLabel()
        self._model_vendor_label = QLabel()
        self.base_url_label = QLabel()
        self.api_key_label = QLabel()
        self.model_label = QLabel()
        self._profile_timeout_label = QLabel()
        form.addRow(self._profile_name_label, self.profile_name_edit)
        form.addRow(self._provider_label, self.provider_combo)
        form.addRow(self._model_vendor_label, self.model_vendor_combo)
        form.addRow(self.base_url_label, self.base_url_edit)
        form.addRow(self.api_key_label, self.api_key_edit)
        form.addRow(self.model_label, self.model_edit)
        form.addRow(self._profile_timeout_label, self.timeout_spin)
        editor_layout.addLayout(form)

        self.profile_help = QLabel()
        self.profile_help.setWordWrap(True)
        self.profile_help.setObjectName("inlineNotice")
        editor_layout.addWidget(self.profile_help)
        self._profile_warning = QLabel()
        self._profile_warning.setWordWrap(True)
        self._profile_warning.setObjectName("warningNotice")
        editor_layout.addWidget(self._profile_warning)
        self.test_button = QPushButton()
        self.test_status = QLabel()
        self.test_status.setObjectName("translationTestStatus")
        self.test_status.setWordWrap(True)
        self.test_status.hide()
        action_row.insertWidget(1, self.test_button)
        management_layout.addWidget(self.test_status)
        layout.addWidget(editor_card)
        editor_card.hide()
        layout.addStretch()

        self.profile_combo.currentIndexChanged.connect(self._profile_selected)
        self.new_profile_button.clicked.connect(self._new_profile)
        self.delete_profile_button.clicked.connect(self._delete_profile)
        self.test_button.clicked.connect(self.test_requested)
        self.profile_name_edit.editingFinished.connect(self._profile_name_finished)

    def retranslate(self) -> None:
        self._management_title.setText(self._i18n.tr("profile_management.title"))
        self._management_subtitle.setText(
            self._i18n.tr("profile_management.subtitle")
        )
        self._profile_column.setText(self._i18n.tr("profile_management.profile"))
        self._service_column.setText(self._i18n.tr("profile_management.service"))
        self._actions_column.setText(self._i18n.tr("profile_management.actions"))
        self._profile_card_title.setText(self._i18n.tr("profile.editor_title"))
        self._profile_name_label.setText(self._i18n.tr("profile.name"))
        self._provider_label.setText(self._i18n.tr("profile.provider"))
        self._model_vendor_label.setText(self._i18n.tr("profile.model_vendor"))
        self.base_url_label.setText(self._i18n.tr("profile.interface"))
        self._profile_timeout_label.setText(self._i18n.tr("profile.timeout"))
        self.new_profile_button.setText(self._i18n.tr("profile.new_model"))
        self.delete_profile_button.setText(self._i18n.tr("profile.delete"))
        self.test_button.setText(self._i18n.tr("profile.test_button"))
        self._rebuild_provider_combo()
        self._rebuild_vendor_combo()
        self._rebuild_profile_list(self._loaded_profile_id)
        self._update_provider_fields()

    def load_profiles(self, profiles: list[TranslationProfile]) -> None:
        self._profiles = deepcopy(profiles)
        self._loaded_profile_id = ""
        self.profile_combo.blockSignals(True)
        self.profile_combo.clear()
        for profile in self._profiles:
            self.profile_combo.addItem(profile.name, profile.id)
        self.profile_combo.blockSignals(False)
        self._rebuild_profile_list()
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
        if self._syncing_selection:
            return
        if self._loaded_profile_id:
            if self._commit_profile_editor():
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
        vendor = str(profile.options.get("model_vendor", ""))
        if not vendor and profile.provider in LARGE_MODEL_PROVIDERS:
            vendor = model_vendor_from_profile(profile.base_url, profile.name)
        set_combo(self.model_vendor_combo, vendor or "custom")
        self.base_url_edit.setText(profile.base_url)
        self.api_key_edit.setText(profile.api_key)
        self.model_edit.setText(profile.model)
        self.timeout_spin.setValue(profile.timeout_seconds)
        self._select_profile_row(profile_id)
        self._update_provider_fields()

    def _commit_profile_editor(self) -> bool:
        if not self._loaded_profile_id:
            return False
        try:
            profile = self._profile(self._loaded_profile_id)
        except KeyError:
            return False
        before = deepcopy(profile)
        profile.name = (
            self.profile_name_edit.text().strip()
            or self._i18n.tr("translation.unnamed_profile")
        )
        profile.provider = str(self.provider_combo.currentData())
        profile.base_url = self.base_url_edit.text().strip()
        profile.api_key = self.api_key_edit.text().strip()
        profile.model = self.model_edit.text().strip()
        profile.timeout_seconds = float(self.timeout_spin.value())
        if profile.provider in LARGE_MODEL_PROVIDERS:
            profile.options["model_vendor"] = str(
                self.model_vendor_combo.currentData() or "custom"
            )
        index = self.profile_combo.findData(profile.id)
        if index >= 0:
            self.profile_combo.setItemText(index, profile.name)
        label = self._profile_name_labels.get(profile.id)
        if label is not None:
            label.setText(profile.name)
        return profile != before

    def _new_profile(self) -> None:
        self._commit_profile_editor()
        dialog = AddProfileDialog(self._i18n, self)
        if dialog.exec() != QDialog.DialogCode.Accepted or dialog.profile is None:
            return
        self._append_profile(dialog.profile)

    def _append_profile(self, profile: TranslationProfile) -> None:
        self._profiles.append(deepcopy(profile))
        self.profile_combo.addItem(profile.name, profile.id)
        self._rebuild_profile_list(profile.id)
        self.profile_combo.setCurrentIndex(self.profile_combo.count() - 1)
        self.profiles_changed.emit()
        self.structure_changed.emit()

    def _delete_profile(self) -> None:
        if len(self._profiles) <= 1:
            self.set_test_status(self._i18n.tr("profile.min_one"), True)
            return
        profile_id = str(self.profile_combo.currentData())
        self._profiles = [item for item in self._profiles if item.id != profile_id]
        self._loaded_profile_id = ""
        self.profile_combo.removeItem(self.profile_combo.currentIndex())
        self._rebuild_profile_list(str(self.profile_combo.currentData() or ""))
        self._profile_selected()
        self.profiles_changed.emit()
        self.structure_changed.emit()

    def _rebuild_provider_combo(self) -> None:
        current = self.provider_combo.currentData()
        self.provider_combo.blockSignals(True)
        self.provider_combo.clear()
        for key, value in PROVIDERS:
            self.provider_combo.addItem(self._i18n.tr(key), value)
        set_combo(self.provider_combo, str(current or ""))
        self.provider_combo.blockSignals(False)

    def _rebuild_vendor_combo(self) -> None:
        current = self.model_vendor_combo.currentData()
        self.model_vendor_combo.clear()
        for key, value, _base_url in MODEL_VENDORS:
            self.model_vendor_combo.addItem(self._i18n.tr(key), value)
        self.model_vendor_combo.addItem(
            self._i18n.tr("model_vendor.custom"), "custom"
        )
        set_combo(self.model_vendor_combo, str(current or "custom"))

    def _rebuild_profile_list(self, selected_id: str = "") -> None:
        while self.profile_list_layout.count():
            item = self.profile_list_layout.takeAt(0)
            widget = item.widget()
            if widget is not None:
                widget.setParent(None)
                widget.deleteLater()
        self._profile_rows.clear()
        self._profile_name_labels.clear()
        self._profile_row_parts.clear()
        groups = (
            ("builtin", "profile_group.builtin"),
            ("machine", "profile_group.machine"),
            ("model", "profile_group.model"),
        )
        for group_key, title_key in groups:
            profiles = [
                profile
                for profile in self._profiles
                if self._group_for(profile) == group_key
            ]
            if not profiles and group_key != "builtin":
                continue
            title = QLabel(self._i18n.tr(title_key))
            title.setObjectName("profileGroupTitle")
            self.profile_list_layout.addWidget(title)
            for profile in profiles:
                self.profile_list_layout.addWidget(self._profile_row(profile))
        self._select_profile_row(selected_id)
        self._apply_responsive_layout(force=True)

    def _profile_row(self, profile: TranslationProfile) -> QFrame:
        frame = QFrame()
        frame.setObjectName("profileManagementRow")
        row = QGridLayout(frame)
        row.setContentsMargins(14, 5, 10, 5)
        row.setHorizontalSpacing(12)
        select = QPushButton(profile.name)
        select.setObjectName("profileRowSelectButton")
        select.setToolTip(profile.name)
        select.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Fixed,
        )
        service = QLabel(self._service_name(profile))
        service.setObjectName("profileRowService")
        service.setWordWrap(True)
        service.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Preferred,
        )
        actions = QWidget()
        actions_layout = QHBoxLayout(actions)
        actions_layout.setContentsMargins(0, 0, 0, 0)
        actions_layout.setSpacing(4)
        edit = QPushButton(self._i18n.tr("profile_management.edit"))
        edit.setObjectName("tableActionButton")
        delete = QPushButton(self._i18n.tr("profile_management.delete"))
        delete.setObjectName("tableDangerButton")
        select.clicked.connect(
            lambda _checked=False, value=profile.id: self._select_from_list(value)
        )
        edit.clicked.connect(
            lambda _checked=False, value=profile.id: self._edit_from_list(value)
        )
        delete.clicked.connect(
            lambda _checked=False, value=profile.id: self._delete_from_list(value)
        )
        actions_layout.addWidget(edit)
        actions_layout.addWidget(delete)
        row.addWidget(select, 0, 0)
        row.addWidget(service, 0, 1)
        row.addWidget(actions, 0, 2)
        row.setColumnStretch(0, 2)
        row.setColumnStretch(1, 2)
        row.setColumnStretch(2, 0)
        self._profile_rows[profile.id] = frame
        self._profile_name_labels[profile.id] = select
        self._profile_row_parts[profile.id] = (
            row,
            select,
            service,
            actions,
        )
        return frame

    def resizeEvent(self, event: QResizeEvent) -> None:
        super().resizeEvent(event)
        self._apply_responsive_layout()

    def _apply_responsive_layout(self, *, force: bool = False) -> None:
        width = self.scroll.viewport().width()
        compact = width < 620
        if not force and compact == self._compact:
            return
        self._compact = compact
        self.profile_list_header.setVisible(not compact)
        for row, select, service, actions in self._profile_row_parts.values():
            for widget in (select, service, actions):
                row.removeWidget(widget)
            if compact:
                row.addWidget(select, 0, 0, 1, 2)
                row.addWidget(service, 1, 0)
                row.addWidget(actions, 1, 1)
                row.setColumnStretch(0, 1)
                row.setColumnStretch(1, 0)
                row.setColumnStretch(2, 0)
            else:
                row.addWidget(select, 0, 0)
                row.addWidget(service, 0, 1)
                row.addWidget(actions, 0, 2)
                row.setColumnStretch(0, 2)
                row.setColumnStretch(1, 2)
                row.setColumnStretch(2, 0)
            row.setColumnMinimumWidth(0, 0)
            row.setColumnMinimumWidth(1, 0)
            row.setColumnMinimumWidth(2, 0)

    def _select_from_list(self, profile_id: str) -> None:
        index = self.profile_combo.findData(profile_id)
        if index >= 0:
            self.profile_combo.setCurrentIndex(index)

    def _select_profile_row(self, profile_id: str) -> None:
        for value, frame in self._profile_rows.items():
            selected = value == profile_id
            frame.setProperty("selected", selected)
            frame.style().unpolish(frame)
            frame.style().polish(frame)

    def _edit_from_list(self, profile_id: str) -> None:
        index = self.profile_combo.findData(profile_id)
        if index < 0:
            return
        self.profile_combo.setCurrentIndex(index)
        dialog = AddProfileDialog(
            self._i18n,
            self,
            profile=deepcopy(self._profile(profile_id)),
        )
        if dialog.exec() != QDialog.DialogCode.Accepted or dialog.profile is None:
            return
        profile_index = next(
            (
                position
                for position, profile in enumerate(self._profiles)
                if profile.id == profile_id
            ),
            -1,
        )
        if profile_index < 0:
            return
        self._profiles[profile_index] = deepcopy(dialog.profile)
        self.profile_combo.setItemText(index, dialog.profile.name)
        self._loaded_profile_id = ""
        self._profile_selected()
        self._rebuild_profile_list(profile_id)
        self.profiles_changed.emit()
        self.structure_changed.emit()

    def _delete_from_list(self, profile_id: str) -> None:
        index = self.profile_combo.findData(profile_id)
        if index < 0:
            return
        self.profile_combo.setCurrentIndex(index)
        self.delete_profile_button.click()

    def _profile_name_finished(self) -> None:
        self._commit_profile_editor()
        self._rebuild_profile_list(self._loaded_profile_id)

    @staticmethod
    def _group_for(profile: TranslationProfile) -> str:
        if profile.provider == "test":
            return "builtin"
        if profile.provider in LARGE_MODEL_PROVIDERS:
            return "model"
        return "machine"

    def _service_name(self, profile: TranslationProfile) -> str:
        if profile.provider in LARGE_MODEL_PROVIDERS:
            vendor = str(profile.options.get("model_vendor", "")) or model_vendor_from_profile(
                profile.base_url, profile.name
            )
            index = self.model_vendor_combo.findData(vendor)
            vendor_name = (
                self.model_vendor_combo.itemText(index)
                if index >= 0
                else self._i18n.tr("model_vendor.custom")
            )
            capability = self._i18n.tr(
                "profile_add.visual_model"
                if profile.provider == "multimodal_openai"
                else "profile_add.text_model"
            )
            return f"{vendor_name} · {capability}"
        for key, value in PROVIDERS:
            if value == profile.provider:
                return self._i18n.tr(key)
        return profile.provider

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
        is_large_model = provider in LARGE_MODEL_PROVIDERS
        show_base_url = provider in {
            "deepl",
            "google_cloud",
            "google_free",
            "aliyun",
            "openai_compatible",
            "multimodal_openai",
            "tencent",
        }
        show_api_key = provider in {
            "deepl",
            "google_cloud",
            "aliyun",
            "openai_compatible",
            "multimodal_openai",
            "tencent",
        }
        show_model = is_large_model or provider in {"tencent", "aliyun"}
        for widget in (self._model_vendor_label, self.model_vendor_combo):
            widget.setVisible(is_large_model)
        for widget in (self.base_url_label, self.base_url_edit):
            widget.setVisible(show_base_url)
        for widget in (self.api_key_label, self.api_key_edit):
            widget.setVisible(show_api_key)
        for widget in (self.model_label, self.model_edit):
            widget.setVisible(show_model)
        self.api_key_label.setText(
            self._i18n.tr(
                "profile.secret_id"
                if provider == "tencent"
                else "profile.aliyun_access_key_id"
                if provider == "aliyun"
                else "profile.api_key"
            )
        )
        self.model_label.setText(
            self._i18n.tr(
                "profile.secret_key"
                if provider == "tencent"
                else "profile.aliyun_access_key_secret"
                if provider == "aliyun"
                else "profile.model"
            )
        )
        self._update_provider_placeholders()
        if provider == "google_free" and not self.base_url_edit.text().strip():
            self.base_url_edit.setText(GOOGLE_FREE_ENDPOINT)
        elif provider == "tencent" and not self.base_url_edit.text().strip():
            self.base_url_edit.setText(TENCENT_TRANSLATION_ENDPOINT)

    def _update_provider_placeholders(self) -> None:
        provider = str(self.provider_combo.currentData())
        api_keys = {
            "deepl": "profile.placeholder_key",
            "google_cloud": "profile.placeholder_key",
            "aliyun": "profile.placeholder_aliyun_id",
            "openai_compatible": "profile.placeholder_key",
            "multimodal_openai": "profile.placeholder_key",
            "tencent": "profile.placeholder_tencent_id",
        }
        model_keys = {
            "openai_compatible": "profile.placeholder_model",
            "multimodal_openai": "profile.placeholder_model",
            "tencent": "profile.placeholder_tencent_key",
            "aliyun": "profile.placeholder_aliyun_secret",
        }
        base_urls = {
            "google_free": "profile.placeholder_google_free",
            "tencent": "profile.placeholder_tencent_url",
            "aliyun": "profile.placeholder_aliyun_endpoint",
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
            "aliyun": "profile.help_aliyun",
            "openai_compatible": "profile.help_openai",
            "multimodal_openai": "profile.help_multimodal",
        }
        self.profile_help.setText(
            self._i18n.tr(help_keys.get(provider, "profile.help_unknown"))
        )
