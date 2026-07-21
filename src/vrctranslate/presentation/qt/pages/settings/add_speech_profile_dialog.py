from __future__ import annotations

from copy import deepcopy
from uuid import uuid4

from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QComboBox,
    QFormLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.application.speech_profiles import creatable_speech_services
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.options import languages
from vrctranslate.presentation.qt.widgets import NoWheelComboBox


_CUSTOM_TENCENT_ENGINE = "__custom_tencent_engine__"


class AddSpeechProfileDialog(QDialog):
    """Create or edit one provider-specific realtime ASR profile."""

    def __init__(
        self,
        i18n: I18nManager,
        parent: QWidget | None = None,
        *,
        profile: SpeechRecognitionProfile | None = None,
    ) -> None:
        super().__init__(parent)
        self._i18n = i18n
        self._source = deepcopy(profile) if profile is not None else None
        self._profile: SpeechRecognitionProfile | None = None
        self.setObjectName("addProfileDialog")
        self.setModal(True)
        self.setMinimumWidth(520)
        self._build_ui()
        self._retranslate()
        if self._source is not None:
            self._load_profile(self._source)

    @property
    def profile(self) -> SpeechRecognitionProfile | None:
        return self._profile

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(22, 20, 22, 18)
        root.setSpacing(14)
        self._title = QLabel()
        self._title.setObjectName("dialogTitle")
        root.addWidget(self._title)
        self.form = QFormLayout()
        self.form.setVerticalSpacing(12)
        self.provider_combo = NoWheelComboBox()
        self.profile_name_edit = QLineEdit()
        self.field_one_edit = QLineEdit()
        self.field_two_edit = QLineEdit()
        self.field_three_edit = QLineEdit()
        self.model_combo = NoWheelComboBox()
        self.language_combo = NoWheelComboBox()
        self.model_combo.setMinimumWidth(0)
        self.model_combo.setMinimumContentsLength(24)
        self.model_combo.setSizeAdjustPolicy(
            QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon
        )
        self.model_combo.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Fixed,
        )
        self.custom_model_edit = QLineEdit()
        self.legacy_model_edit = QLineEdit()
        self._provider_label = QLabel()
        self._name_label = QLabel()
        self._field_one_label = QLabel()
        self._field_two_label = QLabel()
        self._field_three_label = QLabel()
        self._model_label = QLabel()
        self._language_label = QLabel()
        self._custom_model_label = QLabel()
        self._legacy_model_label = QLabel()
        self.form.addRow(self._provider_label, self.provider_combo)
        self.form.addRow(self._name_label, self.profile_name_edit)
        self.form.addRow(self._field_one_label, self.field_one_edit)
        self.form.addRow(self._field_two_label, self.field_two_edit)
        self.form.addRow(self._field_three_label, self.field_three_edit)
        self.form.addRow(self._model_label, self.model_combo)
        self.form.addRow(self._language_label, self.language_combo)
        self.form.addRow(self._custom_model_label, self.custom_model_edit)
        self.form.addRow(self._legacy_model_label, self.legacy_model_edit)
        root.addLayout(self.form)
        self.provider_help = QLabel()
        self.provider_help.setObjectName("fieldHint")
        self.provider_help.setWordWrap(True)
        root.addWidget(self.provider_help)
        self.buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Save
            | QDialogButtonBox.StandardButton.Cancel
        )
        self.buttons.accepted.connect(self._accept_profile)
        self.buttons.rejected.connect(self.reject)
        root.addWidget(self.buttons)
        self.provider_combo.currentIndexChanged.connect(self._provider_changed)
        self.model_combo.currentIndexChanged.connect(self._model_changed)

    def _retranslate(self) -> None:
        t = self._i18n.tr
        editing = self._source is not None
        title_key = "speech_profile.edit_title" if editing else "speech_profile_add.title"
        self.setWindowTitle(t(title_key))
        self._title.setText(t(title_key))
        self._provider_label.setText(t("speech_profile.service"))
        self._name_label.setText(t("voice_settings.profile_name"))
        self._language_label.setText(t("route.source"))
        current_provider = str(self.provider_combo.currentData() or "")
        self.provider_combo.blockSignals(True)
        self.provider_combo.clear()
        descriptors = creatable_speech_services()
        if self._source is not None and not any(
            item.provider == self._source.provider for item in descriptors
        ):
            self.provider_combo.addItem(
                t(f"speech_profile.provider.{self._source.provider}"),
                self._source.provider,
            )
        else:
            for descriptor in descriptors:
                self.provider_combo.addItem(
                    t(f"speech_profile.provider.{descriptor.provider}"),
                    descriptor.provider,
                )
        index = self.provider_combo.findData(
            self._source.provider if self._source is not None else current_provider
        )
        self.provider_combo.setCurrentIndex(index if index >= 0 else 0)
        self.provider_combo.setEnabled(self._source is None)
        self.provider_combo.blockSignals(False)
        save = self.buttons.button(QDialogButtonBox.StandardButton.Save)
        cancel = self.buttons.button(QDialogButtonBox.StandardButton.Cancel)
        if save is not None:
            save.setText(t("profile_add.save" if editing else "profile_add.create"))
            save.setObjectName("primaryButton")
        if cancel is not None:
            cancel.setText(t("profile_add.cancel"))
        self._provider_changed()

    def _provider_changed(self) -> None:
        provider = str(self.provider_combo.currentData() or "")
        t = self._i18n.tr
        self.field_one_edit.setEchoMode(QLineEdit.EchoMode.Normal)
        self.field_two_edit.setEchoMode(QLineEdit.EchoMode.Normal)
        self.field_three_edit.setEchoMode(QLineEdit.EchoMode.Password)
        self.model_combo.clear()
        current_language = str(self.language_combo.currentData() or "zh-CN")
        self.language_combo.clear()
        for label, code in languages(self._i18n):
            if code != "auto":
                self.language_combo.addItem(label, code)
        language_index = self.language_combo.findData(current_language)
        self.language_combo.setCurrentIndex(
            language_index if language_index >= 0 else 0
        )
        self.form.setRowVisible(self.language_combo, False)
        descriptor = next(
            (item for item in creatable_speech_services() if item.provider == provider),
            None,
        )
        if descriptor is not None:
            for model in descriptor.model_ids:
                label = (
                    t(f"speech_profile.tencent_engine_option.{model}")
                    if provider == "tencent_realtime"
                    else model
                )
                self.model_combo.addItem(f"{label} · {model}", model)
        if provider == "tencent_realtime":
            self.model_combo.addItem(
                t("speech_profile.tencent_engine_custom"),
                _CUSTOM_TENCENT_ENGINE,
            )
            self._field_one_label.setText(t("speech_profile.tencent_app_id"))
            self._field_two_label.setText(t("speech_profile.tencent_secret_id"))
            self._field_three_label.setText(t("speech_profile.tencent_secret_key"))
            self.field_two_edit.setEchoMode(QLineEdit.EchoMode.Password)
            self._model_label.setText(t("speech_profile.tencent_engine"))
            self._custom_model_label.setText(
                t("speech_profile.tencent_engine_parameter")
            )
            self._set_provider_rows(True, True, True, True, False)
            self.provider_help.setText(
                t("speech_profile.tencent_language_help")
            )
        elif provider == "aliyun_nls_realtime":
            self._field_one_label.setText(t("speech_profile.aliyun_app_key"))
            self._field_two_label.setText(t("speech_profile.aliyun_access_id"))
            self._field_three_label.setText(t("speech_profile.aliyun_access_secret"))
            self.field_two_edit.setEchoMode(QLineEdit.EchoMode.Password)
            self._set_provider_rows(True, True, True, False, False)
            self.form.setRowVisible(self.language_combo, True)
            self.provider_help.setText(
                t("speech_profile.aliyun_language_help")
            )
        elif provider == "local_offline":
            self._model_label.setText(t("voice_settings.model"))
            self._set_provider_rows(False, False, False, True, False)
            self.provider_help.setText(t("speech_profile.local_help"))
        else:
            self._field_one_label.setText(t("speech_profile.legacy_credential"))
            self.field_one_edit.setEchoMode(QLineEdit.EchoMode.Password)
            self._legacy_model_label.setText(t("voice_settings.model"))
            self._set_provider_rows(True, False, False, False, True)
            self.provider_help.clear()
        if not self.profile_name_edit.text().strip():
            self.profile_name_edit.setPlaceholderText(self.provider_combo.currentText())
        self._model_changed()

    def _set_provider_rows(
        self,
        one: bool,
        two: bool,
        three: bool,
        model_combo: bool,
        legacy_model: bool,
    ) -> None:
        for field, visible in (
            (self.field_one_edit, one),
            (self.field_two_edit, two),
            (self.field_three_edit, three),
            (self.model_combo, model_combo),
            (self.legacy_model_edit, legacy_model),
        ):
            self.form.setRowVisible(field, visible)
        if not model_combo:
            self.form.setRowVisible(self.custom_model_edit, False)

    def _model_changed(self) -> None:
        custom = (
            str(self.provider_combo.currentData() or "") == "tencent_realtime"
            and self.model_combo.currentData() == _CUSTOM_TENCENT_ENGINE
        )
        self.form.setRowVisible(self.custom_model_edit, custom)

    def _load_profile(self, profile: SpeechRecognitionProfile) -> None:
        index = self.provider_combo.findData(profile.provider)
        if index >= 0:
            self.provider_combo.setCurrentIndex(index)
        self.profile_name_edit.setText(profile.name)
        if profile.provider == "tencent_realtime":
            self.field_one_edit.setText(str(profile.options.get("app_id", "")))
            self.field_two_edit.setText(str(profile.options.get("secret_id", "")))
            self.field_three_edit.setText(profile.api_key)
        elif profile.provider == "aliyun_nls_realtime":
            self.field_one_edit.setText(str(profile.options.get("app_key", "")))
            self.field_two_edit.setText(
                str(profile.options.get("access_key_id", ""))
            )
            self.field_three_edit.setText(profile.api_key)
            language_index = self.language_combo.findData(
                str(profile.options.get("language", "zh-CN"))
            )
            if language_index >= 0:
                self.language_combo.setCurrentIndex(language_index)
        else:
            self.field_one_edit.setText(profile.api_key)
            self.legacy_model_edit.setText(profile.model)
        model_index = self.model_combo.findData(profile.model)
        if model_index >= 0:
            self.model_combo.setCurrentIndex(model_index)
        elif profile.provider == "tencent_realtime" and profile.model:
            custom_index = self.model_combo.findData(_CUSTOM_TENCENT_ENGINE)
            self.model_combo.setCurrentIndex(custom_index)
            self.custom_model_edit.setText(profile.model)
        self._model_changed()

    def _accept_profile(self) -> None:
        provider = str(self.provider_combo.currentData() or "")
        values = self._profile_values(provider)
        if values is None:
            QMessageBox.warning(
                self,
                self._i18n.tr("profile_add.incomplete_title"),
                self._i18n.tr("profile_add.incomplete"),
            )
            return
        api_key, model, provider_options = values
        profile = deepcopy(self._source) if self._source is not None else SpeechRecognitionProfile(
            id=f"speech-{uuid4().hex[:8]}"
        )
        profile.name = self.profile_name_edit.text().strip() or self.provider_combo.currentText()
        profile.provider = provider
        profile.base_url = ""
        profile.api_key = api_key
        profile.model = model
        profile.timeout_seconds = 8.0
        options = dict(profile.options)
        for key in (
            "app_id",
            "secret_id",
            "app_key",
            "access_key_id",
            "access_token",
            "token_endpoint",
            "language",
        ):
            options.pop(key, None)
        options.update(provider_options)
        options["validation_state"] = str(
            options.get("validation_state", "pending")
        )
        profile.options = options
        self._profile = profile
        self.accept()

    def _profile_values(
        self, provider: str
    ) -> tuple[str, str, dict[str, str]] | None:
        one = self.field_one_edit.text().strip()
        two = self.field_two_edit.text().strip()
        three = self.field_three_edit.text().strip()
        model = str(self.model_combo.currentData() or "")
        if model == _CUSTOM_TENCENT_ENGINE:
            model = self.custom_model_edit.text().strip()
        if provider == "tencent_realtime":
            return (
                (three, model, {"service_vendor": "tencent", "app_id": one, "secret_id": two})
                if one and two and three and model
                else None
            )
        if provider == "aliyun_nls_realtime":
            return (
                (
                    three,
                    "nls-realtime",
                    {
                        "service_vendor": "aliyun",
                        "app_key": one,
                        "access_key_id": two,
                        "language": str(
                            self.language_combo.currentData() or "zh-CN"
                        ),
                    },
                )
                if one and two and three
                else None
            )
        if provider == "local_offline":
            return ("", model, {"service_vendor": "local"}) if model else None
        if self._source is not None:
            return one, self.legacy_model_edit.text().strip(), {}
        return None
