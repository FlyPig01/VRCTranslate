from __future__ import annotations

from PySide6.QtWidgets import (
    QCheckBox,
    QFormLayout,
    QLabel,
    QLineEdit,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.common import card, scroll_page
from vrctranslate.presentation.qt.widgets import NumericLineEdit


class OscSettingsPage(QWidget):
    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        scroll, _, layout = scroll_page()
        root.addWidget(scroll)

        connection, connection_layout = card("")
        self._connection_card_title = connection_layout.itemAt(0).widget()
        self._connection_card_title.setObjectName("cardTitle")
        form = QFormLayout()
        self.osc_host_edit = QLineEdit()
        self.osc_port_spin = NumericLineEdit(1, 65535)
        self.osc_interval_spin = NumericLineEdit(0.1, 60.0, 1)
        self.osc_limit_spin = NumericLineEdit(1, 10000)
        self.osc_sound_check = QCheckBox()
        form.addRow(self._i18n.tr("osc.host"), self.osc_host_edit)
        form.addRow(self._i18n.tr("osc.port"), self.osc_port_spin)
        form.addRow(self._i18n.tr("osc.interval"), self.osc_interval_spin)
        form.addRow(self._i18n.tr("osc.limit"), self.osc_limit_spin)
        form.addRow(self.osc_sound_check)
        connection_layout.addLayout(form)
        self._typing_note = QLabel()
        self._typing_note.setWordWrap(True)
        self._typing_note.setObjectName("inlineNotice")
        connection_layout.addWidget(self._typing_note)
        layout.addWidget(connection)

        overlay, overlay_layout = card("")
        self._overlay_card_title = overlay_layout.itemAt(0).widget()
        self._overlay_card_title.setObjectName("cardTitle")
        overlay_form = QFormLayout()
        self.input_topmost_check = QCheckBox()
        self.input_width_spin = NumericLineEdit(320, 1200)
        overlay_form.addRow(self.input_topmost_check)
        overlay_form.addRow(self._i18n.tr("osc.width"), self.input_width_spin)
        overlay_layout.addLayout(overlay_form)
        self._overlay_note = QLabel()
        self._overlay_note.setWordWrap(True)
        self._overlay_note.setObjectName("inlineNotice")
        overlay_layout.addWidget(self._overlay_note)
        layout.addWidget(overlay)
        layout.addStretch()

        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _retranslate(self) -> None:
        self._connection_card_title.setText(self._i18n.tr("osc.connection_card"))
        self._typing_note.setText(self._i18n.tr("osc.typing_note"))
        self.osc_sound_check.setText(self._i18n.tr("osc.sound"))
        self._overlay_card_title.setText(self._i18n.tr("osc.overlay_card"))
        self.input_topmost_check.setText(self._i18n.tr("osc.topmost"))
        self._overlay_note.setText(self._i18n.tr("osc.note"))

    def load_settings(self, settings: AppSettings) -> None:
        self.osc_host_edit.setText(settings.osc.host)
        self.osc_port_spin.setValue(settings.osc.port)
        self.osc_interval_spin.setValue(settings.osc.min_interval_seconds)
        self.osc_limit_spin.setValue(settings.osc.chatbox_max_units)
        self.osc_sound_check.setChecked(settings.osc.play_sound)
        self.input_topmost_check.setChecked(settings.ui.input_topmost)
        self.input_width_spin.setValue(settings.ui.input_width)

    def collect_settings(self, settings: AppSettings) -> None:
        settings.osc.host = self.osc_host_edit.text().strip() or "127.0.0.1"
        settings.osc.port = int(self.osc_port_spin.value())
        settings.osc.min_interval_seconds = float(self.osc_interval_spin.value())
        settings.osc.chatbox_max_units = int(self.osc_limit_spin.value())
        settings.osc.play_sound = self.osc_sound_check.isChecked()
        settings.ui.input_topmost = self.input_topmost_check.isChecked()
        settings.ui.input_width = int(self.input_width_spin.value())
