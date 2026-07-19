from __future__ import annotations

from copy import deepcopy

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QMouseEvent, QResizeEvent
from PySide6.QtWidgets import (
    QDialog,
    QFrame,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QProgressBar,
    QPushButton,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings, SpeechRecognitionProfile
from vrctranslate.application.speech_profiles import (
    profile_fingerprint,
    profile_validation_state,
    set_profile_validation,
    speech_service_descriptor,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.add_speech_profile_dialog import (
    AddSpeechProfileDialog,
)
from vrctranslate.presentation.qt.pages.settings.common import card, form_layout, scroll_page
from vrctranslate.presentation.qt.widgets import NumericLineEdit


class _SpeechProfileRow(QFrame):
    activate_requested = Signal(str)
    validate_requested = Signal(str)
    edit_requested = Signal(str)
    delete_requested = Signal(str)

    def __init__(
        self,
        profile: SpeechRecognitionProfile,
        i18n: I18nManager,
        provider_name: str,
        state: str,
        active: bool,
    ) -> None:
        super().__init__()
        self.profile_id = profile.id
        self._i18n = i18n
        self.setObjectName("profileManagementRow")
        self.setProperty("active", active)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Minimum,
        )
        self.layout_grid = QGridLayout(self)
        self.layout_grid.setContentsMargins(14, 8, 10, 8)
        self.layout_grid.setHorizontalSpacing(10)
        self.layout_grid.setVerticalSpacing(5)
        self.name_label = QLabel(profile.name)
        self.name_label.setObjectName("profileRowName")
        self.name_label.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.service_label = QLabel(provider_name)
        self.service_label.setObjectName("profileRowService")
        self.service_label.setWordWrap(True)
        self.service_label.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.validation_button = QPushButton()
        self.validation_button.setObjectName("speechValidationButton")
        self.edit_button = QPushButton(i18n.tr("profile_management.edit"))
        self.edit_button.setObjectName("tableActionButton")
        self.delete_button = QPushButton(i18n.tr("profile_management.delete"))
        self.delete_button.setObjectName("tableDangerButton")
        self.validation_button.clicked.connect(
            lambda: self.validate_requested.emit(self.profile_id)
        )
        self.edit_button.clicked.connect(
            lambda: self.edit_requested.emit(self.profile_id)
        )
        self.delete_button.clicked.connect(
            lambda: self.delete_requested.emit(self.profile_id)
        )
        self.set_validation_state(state)
        self.set_compact(False)

    def set_compact(self, compact: bool) -> None:
        for widget in (
            self.name_label,
            self.service_label,
            self.validation_button,
            self.edit_button,
            self.delete_button,
        ):
            self.layout_grid.removeWidget(widget)
        if compact:
            self.layout_grid.addWidget(self.name_label, 0, 0, 1, 3)
            self.layout_grid.addWidget(self.service_label, 1, 0, 1, 3)
            self.layout_grid.addWidget(self.validation_button, 2, 0)
            self.layout_grid.addWidget(self.edit_button, 2, 1)
            self.layout_grid.addWidget(self.delete_button, 2, 2)
            self.layout_grid.setColumnStretch(0, 1)
            self.layout_grid.setColumnStretch(1, 0)
            self.layout_grid.setColumnStretch(2, 0)
        else:
            self.layout_grid.addWidget(self.name_label, 0, 0)
            self.layout_grid.addWidget(self.service_label, 0, 1)
            self.layout_grid.addWidget(self.validation_button, 0, 2)
            self.layout_grid.addWidget(self.edit_button, 0, 3)
            self.layout_grid.addWidget(self.delete_button, 0, 4)
            self.layout_grid.setColumnStretch(0, 2)
            self.layout_grid.setColumnStretch(1, 3)
            self.layout_grid.setColumnStretch(2, 0)
            self.layout_grid.setColumnStretch(3, 0)
            self.layout_grid.setColumnStretch(4, 0)

    def set_validation_state(self, state: str) -> None:
        self.validation_button.setText(
            self._i18n.tr(f"speech_profile.state_{state}")
        )
        self.validation_button.setProperty("state", state)
        self.validation_button.setEnabled(state != "incompatible")
        self.validation_button.style().unpolish(self.validation_button)
        self.validation_button.style().polish(self.validation_button)

    def set_validation_busy(self, busy: bool) -> None:
        self.validation_button.setEnabled(not busy)
        if busy:
            self.validation_button.setText(self._i18n.tr("speech_profile.validating_short"))

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.activate_requested.emit(self.profile_id)
        super().mousePressEvent(event)


class VoiceSettingsPage(QWidget):
    """Cloud and local ASR profiles plus shared audio activity settings."""

    structure_changed = Signal()
    active_profile_changed = Signal()
    test_requested = Signal()
    model_install_requested = Signal()
    model_verify_requested = Signal()
    model_remove_requested = Signal()
    model_cancel_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._profiles: list[SpeechRecognitionProfile] = []
        self._active_profile_id = ""
        self._validation_profile_id = ""
        self._profile_rows: dict[str, _SpeechProfileRow] = {}
        self._validation_labels: dict[str, QPushButton] = {}
        self._compact = False
        self._model_installed = False
        self._model_busy = False
        self._model_version = ""
        self._model_installed_size = 0
        self._model_download_size = 0
        self._model_completed = 0
        self._model_total = 0
        self._model_path = ""
        self._model_error = ""
        self._model_operation = ""
        self._model_removal_pending = False
        self._build_ui()
        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        self.scroll, _, layout = scroll_page()
        self.scroll.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        root.addWidget(self.scroll)

        model, model_layout = card("")
        self._model_title = model_layout.itemAt(0).widget()
        self._model_surface = QFrame()
        self._model_surface.setObjectName("ocrModelCard")
        model_surface_layout = QVBoxLayout(self._model_surface)
        model_surface_layout.setContentsMargins(14, 12, 14, 12)
        model_surface_layout.setSpacing(8)
        model_header = QHBoxLayout()
        self._model_name = QLabel("SenseVoiceSmall INT8")
        self._model_name.setObjectName("ocrModelName")
        self._model_status = QLabel()
        self._model_status.setObjectName("ocrModelStatus")
        model_header.addWidget(self._model_name)
        model_header.addStretch()
        model_header.addWidget(self._model_status)
        model_surface_layout.addLayout(model_header)
        self._model_detail = QLabel()
        self._model_detail.setObjectName("ocrModelDetail")
        self._model_detail.setWordWrap(True)
        model_surface_layout.addWidget(self._model_detail)
        self._model_progress = QProgressBar()
        self._model_progress.setTextVisible(True)
        model_surface_layout.addWidget(self._model_progress)
        model_actions = QHBoxLayout()
        model_actions.addStretch()
        self._model_install = QPushButton()
        self._model_install.setObjectName("primaryButton")
        self._model_verify = QPushButton()
        self._model_remove = QPushButton()
        self._model_cancel = QPushButton()
        model_actions.addWidget(self._model_install)
        model_actions.addWidget(self._model_verify)
        model_actions.addWidget(self._model_remove)
        model_actions.addWidget(self._model_cancel)
        model_surface_layout.addLayout(model_actions)
        model_layout.addWidget(self._model_surface)
        self._model_note = QLabel()
        self._model_note.setObjectName("inlineNotice")
        self._model_note.setWordWrap(True)
        model_layout.addWidget(self._model_note)
        layout.addWidget(model)

        management, management_layout = card("")
        self._management_title = management_layout.itemAt(0).widget()
        management_layout.removeWidget(self._management_title)
        title_row = QHBoxLayout()
        title_row.addWidget(self._management_title)
        title_row.addStretch()
        self.new_profile_button = QPushButton()
        self.new_profile_button.setObjectName("secondaryButton")
        self.new_profile_button.setSizePolicy(
            QSizePolicy.Policy.Fixed,
            QSizePolicy.Policy.Fixed,
        )
        title_row.addWidget(self.new_profile_button)
        management_layout.insertLayout(0, title_row)
        self.header = QFrame()
        self.header.setObjectName("profileListHeader")
        header_layout = QGridLayout(self.header)
        header_layout.setContentsMargins(14, 0, 10, 0)
        header_layout.setHorizontalSpacing(10)
        self._profile_column = QLabel()
        self._service_column = QLabel()
        self._validation_column = QLabel()
        self._edit_column = QLabel()
        self._delete_column = QLabel()
        for index, label in enumerate(
            (
                self._profile_column,
                self._service_column,
                self._validation_column,
                self._edit_column,
                self._delete_column,
            )
        ):
            header_layout.addWidget(label, 0, index)
        header_layout.setColumnStretch(0, 2)
        header_layout.setColumnStretch(1, 3)
        management_layout.addWidget(self.header)
        self.profile_list = QWidget()
        self.profile_list.setObjectName("profileManagementList")
        self.profile_list_layout = QVBoxLayout(self.profile_list)
        self.profile_list_layout.setContentsMargins(0, 0, 0, 0)
        self.profile_list_layout.setSpacing(0)
        management_layout.addWidget(self.profile_list)
        self.validation_notice = QLabel()
        self.validation_notice.setObjectName("warningNotice")
        self.validation_notice.setWordWrap(True)
        self.validation_notice.hide()
        management_layout.addWidget(self.validation_notice)
        layout.addWidget(management)

        segment, segment_layout = card("")
        self._segment_title = segment_layout.itemAt(0).widget()
        segment_form = form_layout()
        self.energy_edit = NumericLineEdit(50, 10000)
        self.silence_edit = NumericLineEdit(200, 3000)
        self.minimum_speech_edit = NumericLineEdit(100, 3000)
        self._energy_label = QLabel()
        self._silence_label = QLabel()
        self._minimum_speech_label = QLabel()
        self._energy_hint = QLabel()
        self._silence_hint = QLabel()
        self._minimum_speech_hint = QLabel()
        segment_form.addRow(
            self._energy_label,
            self._field_with_hint(self.energy_edit, self._energy_hint),
        )
        segment_form.addRow(
            self._silence_label,
            self._field_with_hint(self.silence_edit, self._silence_hint),
        )
        segment_form.addRow(
            self._minimum_speech_label,
            self._field_with_hint(
                self.minimum_speech_edit,
                self._minimum_speech_hint,
            ),
        )
        segment_layout.addLayout(segment_form)
        layout.addWidget(segment)
        layout.addStretch()

        self.new_profile_button.clicked.connect(self._new_profile)
        self._model_install.clicked.connect(self.model_install_requested)
        self._model_verify.clicked.connect(self.model_verify_requested)
        self._model_remove.clicked.connect(self.model_remove_requested)
        self._model_cancel.clicked.connect(self.model_cancel_requested)

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._model_title.setText(t("speech_models.title"))
        self._model_install.setText(t("speech_models.install"))
        self._model_verify.setText(t("speech_models.verify"))
        self._model_remove.setText(t("speech_models.remove"))
        self._model_cancel.setText(t("speech_models.cancel"))
        self._model_note.setText(t("speech_models.note"))
        self._management_title.setText(t("speech_profile.management_title"))
        self._profile_column.setText(t("profile_management.profile"))
        self._service_column.setText(t("speech_profile.service_provider"))
        self._validation_column.setText(t("speech_profile.validation"))
        self._edit_column.setText(t("profile_management.edit"))
        self._delete_column.setText(t("profile_management.delete"))
        self.new_profile_button.setText(t("speech_profile.new"))
        self._segment_title.setText(t("voice_settings.segment_title"))
        self._energy_label.setText(t("voice_settings.energy"))
        self._silence_label.setText(t("voice_settings.silence"))
        self._minimum_speech_label.setText(t("voice_settings.minimum_speech"))
        self._energy_hint.setText(t("voice_settings.energy_help"))
        self._silence_hint.setText(t("voice_settings.silence_help"))
        self._minimum_speech_hint.setText(
            t("voice_settings.minimum_speech_help")
        )
        self._rebuild_profile_list()
        self._render_model()

    def load_settings(self, settings: AppSettings) -> None:
        voice = settings.voice
        voice.ensure_profiles()
        self._profiles = deepcopy(voice.asr_profiles)
        self._active_profile_id = voice.asr_profile_id
        self._validation_profile_id = self._active_profile_id
        self.validation_notice.hide()
        self._rebuild_profile_list()
        self.energy_edit.setValue(voice.segment.energy_threshold)
        self.silence_edit.setValue(voice.segment.silence_ms)
        self.minimum_speech_edit.setValue(voice.segment.minimum_speech_ms)

    def collect_settings(self, settings: AppSettings) -> None:
        voice = settings.voice
        voice.asr_profiles = deepcopy(self._profiles)
        voice.asr_profile_id = self._active_profile_id
        voice.segment.energy_threshold = int(self.energy_edit.value())
        voice.segment.silence_ms = int(self.silence_edit.value())
        voice.segment.minimum_speech_ms = int(self.minimum_speech_edit.value())
        voice.ensure_profiles()

    def selected_profile(self) -> SpeechRecognitionProfile:
        profile_id = self._validation_profile_id or self._active_profile_id
        return deepcopy(self._profile(profile_id))

    def set_validation_busy(self, busy: bool) -> None:
        for profile_id, row in self._profile_rows.items():
            row.set_validation_busy(busy and profile_id == self._validation_profile_id)
            if busy and profile_id != self._validation_profile_id:
                row.validation_button.setEnabled(False)
            elif not busy:
                row.set_validation_state(
                    profile_validation_state(self._profile(profile_id))
                )
        if busy:
            self.validation_notice.setText(
                self._i18n.tr("speech_profile.validating")
            )
            self.validation_notice.show()

    def set_validation_result(
        self,
        profile_id: str,
        state: str,
        message: str,
    ) -> None:
        try:
            profile = self._profile(profile_id)
        except KeyError:
            return
        set_profile_validation(profile, state, message)
        row = self._profile_rows.get(profile_id)
        if row is not None:
            row.set_validation_state(profile_validation_state(profile))
        self.validation_notice.setText(message)
        self.validation_notice.setVisible(bool(message))
        self.structure_changed.emit()

    def set_model_status(
        self,
        installed: bool,
        version: str,
        installed_size: int,
        download_size: int,
        path: str,
        *,
        busy: bool = False,
        error: str = "",
        operation: str = "",
        removal_pending: bool = False,
    ) -> None:
        self._model_installed = installed
        self._model_version = version
        self._model_installed_size = max(0, installed_size)
        self._model_download_size = max(0, download_size)
        self._model_total = max(0, download_size)
        self._model_completed = 0
        self._model_path = path
        self._model_busy = busy
        self._model_error = error
        self._model_operation = operation if busy else ""
        self._model_removal_pending = removal_pending
        self._render_model()

    def set_model_progress(self, completed: int, total: int) -> None:
        self._model_busy = True
        self._model_error = ""
        self._model_operation = "install"
        self._model_completed = max(0, completed)
        self._model_total = max(0, total)
        self._render_model()

    def _render_model(self) -> None:
        t = self._i18n.tr
        mib = 1024 * 1024
        if self._model_removal_pending:
            self._model_status.setText(t("speech_models.removal_pending"))
            self._model_detail.setText(
                t("speech_models.removal_pending_detail", path=self._model_path)
            )
        elif self._model_busy:
            if self._model_operation == "verify":
                self._model_status.setText(t("speech_models.verifying"))
                self._model_detail.setText(t("speech_models.verifying_detail"))
            else:
                self._model_status.setText(t("speech_models.downloading"))
                self._model_detail.setText(
                    t(
                        "speech_models.progress",
                        completed=f"{self._model_completed / mib:.1f}",
                        total=f"{self._model_total / mib:.1f}",
                    )
                )
        elif self._model_error:
            self._model_status.setText(t("speech_models.failed"))
            self._model_detail.setText(self._model_error)
        elif self._model_installed:
            self._model_status.setText(t("speech_models.installed"))
            self._model_detail.setText(
                t(
                    "speech_models.installed_detail",
                    version=self._model_version,
                    size=f"{self._model_installed_size / mib:.1f}",
                    path=self._model_path,
                )
            )
        else:
            self._model_status.setText(t("speech_models.not_installed"))
            self._model_detail.setText(
                t(
                    "speech_models.download_detail",
                    size=f"{self._model_download_size / mib:.1f}",
                )
            )
        total = max(1, self._model_total)
        self._model_progress.setRange(0, total)
        self._model_progress.setValue(min(total, self._model_completed))
        installing = (
            self._model_busy
            and self._model_operation != "verify"
            and not self._model_removal_pending
        )
        self._model_progress.setVisible(installing)
        ready = not self._model_busy and not self._model_removal_pending
        self._model_install.setVisible(not self._model_installed and ready)
        self._model_verify.setVisible(self._model_installed and ready)
        self._model_remove.setVisible(self._model_installed and ready)
        self._model_cancel.setVisible(installing)

    def resizeEvent(self, event: QResizeEvent) -> None:
        super().resizeEvent(event)
        self._apply_responsive_layout()

    def _profile(self, profile_id: str) -> SpeechRecognitionProfile:
        for profile in self._profiles:
            if profile.id == profile_id:
                return profile
        raise KeyError(profile_id)

    def _new_profile(self) -> None:
        dialog = AddSpeechProfileDialog(self._i18n, self)
        if dialog.exec() != QDialog.DialogCode.Accepted or dialog.profile is None:
            return
        profile = deepcopy(dialog.profile)
        self._profiles.append(profile)
        self._active_profile_id = profile.id
        self._validation_profile_id = profile.id
        self._rebuild_profile_list()
        self.structure_changed.emit()

    def _edit_profile(self, profile_id: str) -> None:
        source = self._profile(profile_id)
        before = profile_fingerprint(source)
        dialog = AddSpeechProfileDialog(
            self._i18n,
            self,
            profile=source,
        )
        if dialog.exec() != QDialog.DialogCode.Accepted or dialog.profile is None:
            return
        updated = deepcopy(dialog.profile)
        if profile_fingerprint(updated) != before:
            set_profile_validation(updated, "pending", "")
        self._profiles = [
            updated if profile.id == profile_id else profile
            for profile in self._profiles
        ]
        self._validation_profile_id = profile_id
        self._rebuild_profile_list()
        self.structure_changed.emit()

    def _delete_profile(self, profile_id: str) -> None:
        self._profiles = [
            profile for profile in self._profiles if profile.id != profile_id
        ]
        if self._active_profile_id == profile_id:
            self._active_profile_id = (
                self._profiles[0].id if self._profiles else ""
            )
        if self._validation_profile_id == profile_id:
            self._validation_profile_id = self._active_profile_id
        self.validation_notice.hide()
        self._rebuild_profile_list()
        self.structure_changed.emit()

    def _activate_profile(self, profile_id: str) -> None:
        self._profile(profile_id)
        if self._active_profile_id == profile_id:
            self._validation_profile_id = profile_id
            return
        self._active_profile_id = profile_id
        self._validation_profile_id = profile_id
        self._refresh_active_rows()
        self.structure_changed.emit()
        self.active_profile_changed.emit()

    def _request_validation(self, profile_id: str) -> None:
        if not self._is_caption_eligible(self._profile(profile_id)):
            return
        self._validation_profile_id = profile_id
        self.test_requested.emit()

    def _rebuild_profile_list(self) -> None:
        while self.profile_list_layout.count():
            item = self.profile_list_layout.takeAt(0)
            widget = item.widget()
            if widget is not None:
                widget.setParent(None)
                widget.deleteLater()
        self._profile_rows.clear()
        self._validation_labels.clear()
        for profile in self._profiles:
            row = _SpeechProfileRow(
                profile,
                self._i18n,
                self._provider_name(profile.provider),
                profile_validation_state(profile),
                profile.id == self._active_profile_id,
            )
            row.activate_requested.connect(self._activate_profile)
            row.validate_requested.connect(self._request_validation)
            row.edit_requested.connect(self._edit_profile)
            row.delete_requested.connect(self._delete_profile)
            self.profile_list_layout.addWidget(row)
            self._profile_rows[profile.id] = row
            self._validation_labels[profile.id] = row.validation_button
        self._apply_responsive_layout(force=True)

    def _refresh_active_rows(self) -> None:
        for profile_id, row in self._profile_rows.items():
            row.setProperty("active", profile_id == self._active_profile_id)
            row.style().unpolish(row)
            row.style().polish(row)

    def _apply_responsive_layout(self, *, force: bool = False) -> None:
        width = self.scroll.viewport().width() if self.scroll is not None else self.width()
        compact = width < 720
        if not force and compact == self._compact:
            return
        self._compact = compact
        self.header.setVisible(bool(self._profiles) and not compact)
        for row in self._profile_rows.values():
            row.set_compact(compact)

    @staticmethod
    def _is_caption_eligible(profile: SpeechRecognitionProfile) -> bool:
        descriptor = speech_service_descriptor(profile.provider)
        return bool(descriptor and descriptor.capabilities.caption_eligible)

    def _provider_name(self, provider: str) -> str:
        return self._i18n.tr(f"speech_profile.provider.{provider}")

    @staticmethod
    def _field_with_hint(field: QWidget, hint: QLabel) -> QWidget:
        container = QWidget()
        layout = QVBoxLayout(container)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(4)
        hint.setObjectName("fieldHint")
        hint.setWordWrap(True)
        layout.addWidget(field)
        layout.addWidget(hint)
        return container
