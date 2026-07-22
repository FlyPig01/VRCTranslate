from __future__ import annotations

from math import log10

from PySide6.QtCore import QEasingCurve, QEvent, QPropertyAnimation, QSize, Qt, Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QFormLayout,
    QFrame,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QLayout,
    QProgressBar,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import (
    DEFAULT_QUICK_INPUT_HOTKEY,
    DEFAULT_SELF_VOICE_HOTKEY,
    SelfVoiceSettings,
    TranslationRouteSettings,
    UiSettings,
)
from vrctranslate.domain.speech import MicrophoneDevice
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.options import formats
from vrctranslate.presentation.qt.widgets import (
    ConfirmableHotkeyEdit,
    NoWheelComboBox,
    NumericLineEdit,
    VoiceActivityIndicator,
)


_MICROPHONE_TEST_PHRASES = {
    "zh-CN": "你好，这是麦克风测试。",
    "en": "Hello, this is a microphone test.",
    "ja": "こんにちは、マイクのテストです。",
    "ko": "안녕하세요, 마이크 테스트입니다.",
}


class SelfMessagePage(QWidget):
    input_settings_changed = Signal(bool, int, str, str)
    self_voice_settings_changed = Signal(bool, str, str, str, str)
    microphone_test_requested = Signal()
    hotkey_editing_changed = Signal(bool)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._profile_name = ""
        self._status_text = ""
        self._last_original = ""
        self._last_translated = ""
        self._loading_settings = False
        self._microphones: list[MicrophoneDevice] = []
        self._self_voice_original = ""
        self._active_hotkey_control: ConfirmableHotkeyEdit | None = None
        self._narrow_layout: bool | None = None
        self._build_ui()
        self._retranslate()
        i18n.language_changed.connect(lambda *_: self._retranslate())

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(22, 18, 22, 14)
        root.setSpacing(10)
        self._title = QLabel()
        self._title.setObjectName("pageTitle")
        self._subtitle = QLabel()
        self._subtitle.setObjectName("pageSubtitle")
        self._subtitle.setWordWrap(True)
        root.addWidget(self._title)
        root.addWidget(self._subtitle)

        self._scroll = QScrollArea()
        self._scroll.setObjectName("featurePageScroll")
        self._scroll.setWidgetResizable(True)
        self._scroll.setFrameShape(QFrame.Shape.NoFrame)
        self._scroll.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        self._content = QWidget()
        self._content.setMinimumWidth(0)
        self._content.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Preferred,
        )
        self._content_layout = QGridLayout(self._content)
        layout = self._content_layout
        layout.setSizeConstraint(QLayout.SizeConstraint.SetNoConstraint)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setHorizontalSpacing(12)
        layout.setVerticalSpacing(12)

        self._status_card, status_layout = self._card()
        self._card_title = self._title_label(status_layout)
        self._profile_label = QLabel()
        self._profile_label.setWordWrap(True)
        self._status_label = QLabel()
        self._status_label.setObjectName("statusPill")
        self._status_label.setWordWrap(True)
        status_layout.addWidget(self._profile_label)
        status_layout.addWidget(self._status_label)
        status_layout.addStretch()
        layout.addWidget(self._status_card, 0, 0)

        self._recent_card, recent_layout = self._card()
        self._recent_title = self._title_label(recent_layout)
        self._preview_card = QFrame()
        self._preview_card.setObjectName("previewCard")
        preview_layout = QVBoxLayout(self._preview_card)
        preview_layout.setContentsMargins(14, 12, 14, 12)
        preview_layout.setSpacing(7)
        self._last_original_label = QLabel()
        self._last_original_label.setObjectName("previewOriginal")
        self._last_original_label.setWordWrap(True)
        self._last_translated_label = QLabel()
        self._last_translated_label.setObjectName("previewTranslated")
        self._last_translated_label.setWordWrap(True)
        preview_layout.addWidget(self._last_original_label)
        preview_layout.addWidget(self._last_translated_label)
        recent_layout.addWidget(self._preview_card)
        recent_layout.addStretch()
        layout.addWidget(self._recent_card, 0, 1)

        self._settings_card, settings_layout = self._card()
        self._settings_title = self._title_label(settings_layout)
        form = QFormLayout()
        form.setHorizontalSpacing(18)
        form.setVerticalSpacing(10)
        form.setFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow)
        form.setRowWrapPolicy(QFormLayout.RowWrapPolicy.WrapLongRows)
        self.input_topmost_check = QCheckBox()
        self.input_width_edit = NumericLineEdit(320, 1200)
        self.message_format_combo = NoWheelComboBox()
        self.quick_input_hotkey_control = ConfirmableHotkeyEdit(
            DEFAULT_QUICK_INPUT_HOTKEY
        )
        self.quick_input_hotkey_edit = self.quick_input_hotkey_control.editor
        self._message_format_label = QLabel()
        self._width_label = QLabel()
        self._quick_input_hotkey_label = QLabel()
        self._quick_input_hotkey_status = self._hotkey_status_label()
        form.addRow(self._message_format_label, self.message_format_combo)
        form.addRow(self.input_topmost_check)
        form.addRow(self._width_label, self.input_width_edit)
        form.addRow(
            self._quick_input_hotkey_label,
            self.quick_input_hotkey_control,
        )
        form.addRow(self._quick_input_hotkey_status)
        settings_layout.addLayout(form)
        layout.addWidget(self._settings_card, 1, 0, 1, 2)

        self._voice_card, voice_layout = self._card()
        self._voice_title = self._title_label(voice_layout)
        self._voice_note = QLabel()
        self._voice_note.setWordWrap(True)
        self._voice_note.setObjectName("fieldHint")
        voice_layout.addWidget(self._voice_note)

        self._voice_status_panel = QFrame()
        self._voice_status_panel.setObjectName("selfVoiceStatusPanel")
        self._voice_status_panel.setProperty("state", "idle")
        voice_status_layout = QHBoxLayout(self._voice_status_panel)
        voice_status_layout.setContentsMargins(14, 11, 12, 11)
        voice_status_layout.setSpacing(12)
        self._voice_activity = VoiceActivityIndicator()
        voice_status_layout.addWidget(self._voice_activity)
        voice_status_text = QVBoxLayout()
        voice_status_text.setSpacing(3)
        self._self_voice_status = QLabel()
        self._self_voice_status.setObjectName("selfVoiceStatus")
        self._self_voice_status.setWordWrap(True)
        self._self_voice_original_label = QLabel()
        self._self_voice_original_label.setObjectName("selfVoiceRecent")
        self._self_voice_original_label.setWordWrap(True)
        voice_status_text.addWidget(self._self_voice_status)
        voice_status_text.addWidget(self._self_voice_original_label)
        voice_status_layout.addLayout(voice_status_text, 1)
        self.self_voice_toggle_button = QPushButton()
        self.self_voice_toggle_button.setObjectName("voiceToggleButton")
        self.self_voice_toggle_button.setCheckable(True)
        self.self_voice_toggle_button.setFixedSize(44, 44)
        self.self_voice_toggle_button.setIconSize(QSize(20, 20))
        voice_status_layout.addWidget(self.self_voice_toggle_button)
        voice_layout.addWidget(self._voice_status_panel)

        voice_form = QFormLayout()
        voice_form.setHorizontalSpacing(18)
        voice_form.setVerticalSpacing(10)
        voice_form.setFieldGrowthPolicy(
            QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow
        )
        voice_form.setRowWrapPolicy(QFormLayout.RowWrapPolicy.WrapLongRows)
        self.self_voice_enabled = self.self_voice_toggle_button
        self.microphone_combo = NoWheelComboBox()
        self.microphone_test_button = QPushButton()
        self.microphone_test_button.setObjectName("selfVoiceTestButton")
        self.microphone_test_button.setIcon(load_icon("ui/action_microphone.svg"))
        self.microphone_test_button.setIconSize(QSize(19, 19))
        self.microphone_test_button.setFixedSize(40, 38)
        self.self_voice_language_combo = NoWheelComboBox()
        self.self_voice_scope_combo = NoWheelComboBox()
        self.self_voice_hotkey_control = ConfirmableHotkeyEdit(
            DEFAULT_SELF_VOICE_HOTKEY
        )
        self.self_voice_hotkey_edit = self.self_voice_hotkey_control.editor
        self.microphone_level = QProgressBar()
        self.microphone_level.setObjectName("selfVoiceLevel")
        self.microphone_level.setRange(0, 100)
        self.microphone_level.setValue(0)
        self.microphone_level.setTextVisible(False)
        self.microphone_level.setFixedHeight(14)
        self.microphone_level.setProperty("zone", "normal")
        self._level_animation = QPropertyAnimation(
            self.microphone_level,
            b"value",
            self,
        )
        self._level_animation.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._microphone_label = QLabel()
        self._self_voice_language_label = QLabel()
        self._self_voice_scope_label = QLabel()
        self._microphone_level_label = QLabel()
        self._self_voice_hotkey_label = QLabel()
        self._self_voice_hotkey_status = self._hotkey_status_label()
        microphone_row = QHBoxLayout()
        microphone_row.setContentsMargins(0, 0, 0, 0)
        microphone_row.setSpacing(8)
        microphone_row.addWidget(self.microphone_combo, 1)
        microphone_row.addWidget(self.microphone_test_button)
        voice_form.addRow(self._microphone_label, microphone_row)
        voice_form.addRow(
            self._self_voice_language_label,
            self.self_voice_language_combo,
        )
        voice_form.addRow(self._self_voice_scope_label, self.self_voice_scope_combo)
        voice_form.addRow(
            self._self_voice_hotkey_label,
            self.self_voice_hotkey_control,
        )
        voice_form.addRow(self._self_voice_hotkey_status)
        voice_form.addRow(self._microphone_level_label, self.microphone_level)
        voice_layout.addLayout(voice_form)
        layout.addWidget(self._voice_card, 2, 0, 1, 2)

        layout.setColumnStretch(0, 1)
        layout.setColumnStretch(1, 1)
        layout.setRowStretch(2, 1)
        self._scroll.setWidget(self._content)
        self._scroll.viewport().installEventFilter(self)
        root.addWidget(self._scroll, 1)
        self._apply_responsive_layout()

        self.input_topmost_check.checkStateChanged.connect(self._settings_edited)
        self.input_width_edit.textChanged.connect(self._settings_edited)
        self.message_format_combo.currentIndexChanged.connect(self._settings_edited)
        self.quick_input_hotkey_control.shortcut_confirmed.connect(
            self._settings_edited
        )
        self.self_voice_enabled.toggled.connect(self._self_voice_edited)
        self.self_voice_enabled.toggled.connect(self._sync_voice_toggle_button)
        self.microphone_combo.currentIndexChanged.connect(self._self_voice_edited)
        self.self_voice_language_combo.currentIndexChanged.connect(
            self._self_voice_edited
        )
        self.self_voice_scope_combo.currentIndexChanged.connect(
            self._self_voice_edited
        )
        self.self_voice_hotkey_control.shortcut_confirmed.connect(
            self._self_voice_edited
        )
        for control in (
            self.quick_input_hotkey_control,
            self.self_voice_hotkey_control,
        ):
            control.editing_started.connect(self._hotkey_edit_started)
            control.editing_finished.connect(self._hotkey_edit_finished)
        self.microphone_test_button.clicked.connect(
            self.microphone_test_requested.emit
        )

    @staticmethod
    def _card() -> tuple[QFrame, QVBoxLayout]:
        card = QFrame()
        card.setObjectName("card")
        layout = QVBoxLayout(card)
        layout.setContentsMargins(18, 15, 18, 15)
        layout.setSpacing(9)
        return card, layout

    @staticmethod
    def _title_label(layout: QVBoxLayout) -> QLabel:
        title = QLabel()
        title.setObjectName("cardTitle")
        layout.addWidget(title)
        return title

    @staticmethod
    def _hotkey_status_label() -> QLabel:
        label = QLabel()
        label.setObjectName("hotkeyRegistrationStatus")
        label.setWordWrap(True)
        label.hide()
        return label

    def set_hotkey_registration_status(
        self,
        kind: str,
        state: str = "",
        message: str = "",
    ) -> None:
        label = (
            self._quick_input_hotkey_status
            if kind == "quick_input"
            else self._self_voice_hotkey_status
        )
        label.setProperty("state", state)
        label.setText(message)
        label.setVisible(bool(message))
        label.style().unpolish(label)
        label.style().polish(label)

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._title.setText(t("page.quick_input.title"))
        self._subtitle.setText(t("page.quick_input.subtitle"))
        self._card_title.setText(t("page.quick_input.status_title"))
        self._profile_label.setText(
            t("page.quick_input.profile", name=self._profile_name or "-")
        )
        self._status_label.setText(
            self._status_text or t("page.quick_input.status_waiting")
        )
        self._recent_title.setText(t("page.quick_input.recent_title"))
        if self._last_original or self._last_translated:
            self._last_original_label.setText(
                t("page.quick_input.preview_original", text=self._last_original)
            )
            self._last_translated_label.setText(
                t("page.quick_input.preview_translated", text=self._last_translated)
            )
        else:
            self._last_original_label.setText(t("page.quick_input.no_recent"))
            self._last_translated_label.clear()
        self._settings_title.setText(t("page.quick_input.settings_title"))
        self.input_topmost_check.setText(t("page.quick_input.topmost"))
        self._width_label.setText(t("page.quick_input.width"))
        self._message_format_label.setText(t("route.format"))
        self._rebuild_message_formats()
        self._voice_title.setText(t("self_voice.title"))
        self._voice_note.setText(t("self_voice.note"))
        self._microphone_label.setText(t("self_voice.microphone"))
        self._self_voice_language_label.setText(t("self_voice.language"))
        self._self_voice_scope_label.setText(t("self_voice.scope"))
        self._microphone_level_label.setText(t("self_voice.level"))
        self._quick_input_hotkey_label.setText(t("hotkey.quick_input"))
        self._self_voice_hotkey_label.setText(t("hotkey.self_voice"))
        for control in (
            self.quick_input_hotkey_control,
            self.self_voice_hotkey_control,
        ):
            control.set_labels(
                edit=t("hotkey.edit"),
                restore_default=t("hotkey.restore_default"),
                cancel=t("hotkey.cancel"),
                confirm=t("hotkey.confirm"),
            )
        self._sync_voice_toggle_button()
        if not self.microphone_test_button.property("testing"):
            label = t("self_voice.test_button")
            self.microphone_test_button.setText("")
            self.microphone_test_button.setToolTip(label)
            self.microphone_test_button.setAccessibleName(label)
        self._rebuild_self_voice_languages()
        self._rebuild_self_voice_scopes()
        self.set_microphone_devices(
            self._microphones,
            str(self.microphone_combo.currentData() or ""),
        )
        if not self._self_voice_status.text():
            self._self_voice_status.setText(t("self_voice.status_disabled"))
        self._render_self_voice_original()

    @property
    def has_unsaved_changes(self) -> bool:
        return False

    def set_profile(self, name: str) -> None:
        self._profile_name = name
        self._profile_label.setText(
            self._i18n.tr("page.quick_input.profile", name=name)
        )

    def set_status(self, message: str) -> None:
        self._status_text = message
        self._status_label.setText(message)

    def set_last_translation(self, original: str, translated: str) -> None:
        self._last_original = original
        self._last_translated = translated
        self._retranslate()

    def load_ui_settings(self, settings: UiSettings) -> None:
        self._loading_settings = True
        try:
            self.input_topmost_check.setChecked(settings.input_topmost)
            self.input_width_edit.setValue(settings.input_width)
            self.quick_input_hotkey_control.set_key_sequence(
                settings.quick_input_hotkey
            )
        finally:
            self._loading_settings = False

    def load_route_settings(self, route: TranslationRouteSettings) -> None:
        self._loading_settings = True
        try:
            self._set_combo(self.message_format_combo, route.message_format)
        finally:
            self._loading_settings = False

    def load_self_voice_settings(self, settings: SelfVoiceSettings) -> None:
        self._loading_settings = True
        try:
            self.self_voice_enabled.setChecked(settings.enabled)
            self._set_combo(self.microphone_combo, settings.microphone_id)
            self._set_combo(
                self.self_voice_language_combo,
                settings.source_language,
            )
            self._set_combo(
                self.self_voice_scope_combo,
                settings.activation_scope,
            )
            self.self_voice_hotkey_control.set_key_sequence(
                settings.toggle_hotkey
            )
        finally:
            self._loading_settings = False

    def collect_ui_settings(self, settings: UiSettings) -> None:
        settings.input_topmost = self.input_topmost_check.isChecked()
        settings.input_width = int(self.input_width_edit.value())
        settings.quick_input_hotkey = self.quick_input_hotkey_control.shortcut_text()

    def _settings_edited(self, *_: object) -> None:
        if self._loading_settings:
            return
        try:
            width = int(self.input_width_edit.value())
        except ValueError:
            return
        self.input_settings_changed.emit(
            self.input_topmost_check.isChecked(),
            width,
            str(self.message_format_combo.currentData() or "translation_only"),
            self.quick_input_hotkey_control.shortcut_text(),
        )

    def _self_voice_edited(self, *_: object) -> None:
        if self._loading_settings:
            return
        self.self_voice_settings_changed.emit(
            self.self_voice_enabled.isChecked(),
            str(self.microphone_combo.currentData() or ""),
            str(self.self_voice_language_combo.currentData() or "zh-CN"),
            str(self.self_voice_scope_combo.currentData() or "vrchat_foreground"),
            self.self_voice_hotkey_control.shortcut_text(),
        )

    def set_microphone_devices(
        self,
        devices: list[MicrophoneDevice],
        selected_id: str,
    ) -> None:
        self._microphones = list(devices)
        self.microphone_combo.blockSignals(True)
        self.microphone_combo.clear()
        default_device = next((item for item in devices if item.is_default), None)
        default_name = (
            default_device.name
            if default_device is not None
            else self._i18n.tr("self_voice.microphone_unknown")
        )
        self.microphone_combo.addItem(
            self._i18n.tr("self_voice.microphone_default", name=default_name),
            "",
        )
        if default_device is not None and default_device.host_api:
            self.microphone_combo.setItemData(
                0,
                default_device.host_api,
                Qt.ItemDataRole.ToolTipRole,
            )
        for device in devices:
            if device.is_default:
                continue
            label = device.name
            self.microphone_combo.addItem(label, device.id)
            index = self.microphone_combo.count() - 1
            if device.host_api:
                self.microphone_combo.setItemData(
                    index,
                    device.host_api,
                    Qt.ItemDataRole.ToolTipRole,
                )
        self._set_combo(self.microphone_combo, selected_id)
        self.microphone_combo.blockSignals(False)

    def set_microphone_test_running(self, running: bool) -> None:
        self.microphone_test_button.setProperty("testing", running)
        label = self._i18n.tr(
            "self_voice.test_stop" if running else "self_voice.test_button"
        )
        self.microphone_test_button.setText("")
        self.microphone_test_button.setToolTip(label)
        self.microphone_test_button.setAccessibleName(label)
        self.microphone_test_button.setIcon(
            load_icon(
                "ui/action_pause.svg" if running else "ui/action_microphone.svg"
            )
        )
        self.microphone_test_button.style().unpolish(self.microphone_test_button)
        self.microphone_test_button.style().polish(self.microphone_test_button)

    def microphone_test_phrase(self) -> str:
        language = str(self.self_voice_language_combo.currentData() or "zh-CN")
        return _MICROPHONE_TEST_PHRASES.get(
            language,
            _MICROPHONE_TEST_PHRASES["zh-CN"],
        )

    def set_microphone_level(self, amplitude: int) -> None:
        if amplitude <= 0:
            level = 0
        else:
            decibels = 20 * log10(min(32768, amplitude) / 32768)
            level = round((decibels + 60) * 100 / 60)
        level = min(100, max(0, level))
        current = self.microphone_level.value()
        self._level_animation.stop()
        self._level_animation.setStartValue(current)
        self._level_animation.setEndValue(level)
        self._level_animation.setDuration(90 if level >= current else 220)
        self._level_animation.start()
        zone = "high" if level >= 85 else "medium" if level >= 65 else "normal"
        if self.microphone_level.property("zone") != zone:
            self.microphone_level.setProperty("zone", zone)
            self.microphone_level.style().unpolish(self.microphone_level)
            self.microphone_level.style().polish(self.microphone_level)
        self._voice_activity.set_level(level)

    def set_self_voice_status(self, message: str, state: str = "idle") -> None:
        self._self_voice_status.setText(message)
        self._self_voice_status.setProperty("state", state)
        self._self_voice_status.style().unpolish(self._self_voice_status)
        self._self_voice_status.style().polish(self._self_voice_status)
        self._voice_status_panel.setProperty("state", state)
        self._voice_status_panel.style().unpolish(self._voice_status_panel)
        self._voice_status_panel.style().polish(self._voice_status_panel)
        self._voice_activity.set_state(state)

    def set_self_voice_original(self, original: str) -> None:
        self._self_voice_original = original
        self._render_self_voice_original()

    def _render_self_voice_original(self) -> None:
        self._self_voice_original_label.setText(
            self._i18n.tr(
                "self_voice.last_original",
                text=self._self_voice_original or "-",
            )
        )

    def _rebuild_message_formats(self) -> None:
        current = str(self.message_format_combo.currentData() or "translation_only")
        self.message_format_combo.blockSignals(True)
        self.message_format_combo.clear()
        for label, value in formats(self._i18n):
            self.message_format_combo.addItem(label, value)
        self._set_combo(self.message_format_combo, current)
        self.message_format_combo.blockSignals(False)

    def _rebuild_self_voice_languages(self) -> None:
        current = str(self.self_voice_language_combo.currentData() or "zh-CN")
        self.self_voice_language_combo.blockSignals(True)
        self.self_voice_language_combo.clear()
        for code in ("zh-CN", "en", "ja", "ko"):
            self.self_voice_language_combo.addItem(
                self._i18n.tr(f"lang.{code.replace('-', '_')}"), code
            )
        self._set_combo(self.self_voice_language_combo, current)
        self.self_voice_language_combo.blockSignals(False)

    def _rebuild_self_voice_scopes(self) -> None:
        current = str(
            self.self_voice_scope_combo.currentData() or "vrchat_foreground"
        )
        self.self_voice_scope_combo.blockSignals(True)
        self.self_voice_scope_combo.clear()
        for scope in ("vrchat_foreground", "vrchat_running", "always"):
            self.self_voice_scope_combo.addItem(
                self._i18n.tr(f"self_voice.scope_{scope}"), scope
            )
        self._set_combo(self.self_voice_scope_combo, current)
        self.self_voice_scope_combo.blockSignals(False)

    @staticmethod
    def _set_combo(combo: NoWheelComboBox, value: str) -> None:
        index = combo.findData(value)
        combo.setCurrentIndex(index if index >= 0 else 0)

    def _hotkey_edit_started(self) -> None:
        control = self.sender()
        if not isinstance(control, ConfirmableHotkeyEdit):
            return
        if (
            self._active_hotkey_control is not None
            and self._active_hotkey_control is not control
        ):
            self._active_hotkey_control.cancel_edit()
        self._active_hotkey_control = control
        other = (
            self.self_voice_hotkey_control
            if control is self.quick_input_hotkey_control
            else self.quick_input_hotkey_control
        )
        other.edit_button.setEnabled(False)
        self.hotkey_editing_changed.emit(True)

    def _hotkey_edit_finished(self) -> None:
        control = self.sender()
        if control is self._active_hotkey_control:
            self._active_hotkey_control = None
        self.quick_input_hotkey_control.edit_button.setEnabled(True)
        self.self_voice_hotkey_control.edit_button.setEnabled(True)
        self.hotkey_editing_changed.emit(False)

    def _sync_voice_toggle_button(self, *_: object) -> None:
        enabled = self.self_voice_toggle_button.isChecked()
        self.self_voice_toggle_button.setIcon(
            load_icon("ui/action_pause.svg" if enabled else "ui/action_play.svg")
        )
        self.self_voice_toggle_button.setToolTip(
            self._i18n.tr(
                "self_voice.pause_action" if enabled else "self_voice.start_action"
            )
        )
        self.self_voice_toggle_button.setAccessibleName(
            self.self_voice_toggle_button.toolTip()
        )
        self.self_voice_toggle_button.setProperty("active", enabled)
        self.self_voice_toggle_button.style().unpolish(
            self.self_voice_toggle_button
        )
        self.self_voice_toggle_button.style().polish(self.self_voice_toggle_button)

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        self._apply_responsive_layout()

    def eventFilter(self, watched, event) -> bool:  # type: ignore[no-untyped-def]
        if (
            watched is self._scroll.viewport()
            and event.type() == QEvent.Type.Resize
        ):
            self._apply_responsive_layout()
        return super().eventFilter(watched, event)

    def _apply_responsive_layout(self) -> None:
        viewport_width = self._scroll.viewport().width()
        if viewport_width <= 0:
            viewport_width = max(0, self.width() - 44)
        narrow = viewport_width < 640
        if narrow == self._narrow_layout:
            return
        self._narrow_layout = narrow

        layout = self._content_layout
        for card in (
            self._status_card,
            self._recent_card,
            self._settings_card,
            self._voice_card,
        ):
            layout.removeWidget(card)
        if narrow:
            layout.addWidget(self._status_card, 0, 0, 1, 2)
            layout.addWidget(self._voice_card, 1, 0, 1, 2)
            layout.addWidget(self._settings_card, 2, 0, 1, 2)
            layout.addWidget(self._recent_card, 3, 0, 1, 2)
            layout.setRowStretch(1, 0)
            layout.setRowStretch(3, 1)
        else:
            layout.addWidget(self._status_card, 0, 0)
            layout.addWidget(self._recent_card, 1, 0)
            layout.addWidget(self._settings_card, 2, 0)
            layout.addWidget(self._voice_card, 0, 1, 3, 1)
            layout.setRowStretch(2, 1)
            layout.setRowStretch(3, 0)
