from __future__ import annotations

import platform
import sys
from pathlib import Path

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import QHBoxLayout, QLabel, QPushButton, QVBoxLayout, QWidget

from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.common import card, scroll_page
from vrctranslate.presentation.qt.icon_resources import load_icon


class DataDiagnosticsPage(QWidget):
    clear_logs_requested = Signal()
    open_path_requested = Signal(str)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        scroll, _, layout = scroll_page()
        root.addWidget(scroll)

        data, data_layout = card("")
        self._data_card_title = data_layout.itemAt(0).widget()
        self._data_card_title.setObjectName("cardTitle")
        self.path_label = QLabel()
        self.path_label.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        self.path_label.setWordWrap(True)
        data_layout.addWidget(self.path_label)
        paths_row = QHBoxLayout()
        self.open_data_button = QPushButton()
        self.open_logs_button = QPushButton()
        self.open_cache_button = QPushButton()
        for button in (self.open_data_button, self.open_logs_button, self.open_cache_button):
            button.setIcon(load_icon("ui/action_folder.svg"))
        for button in (self.open_data_button, self.open_logs_button, self.open_cache_button):
            paths_row.addWidget(button)
        data_layout.addLayout(paths_row)
        layout.addWidget(data)

        diagnostics, diagnostics_layout = card("")
        self._diag_card_title = diagnostics_layout.itemAt(0).widget()
        self._diag_card_title.setObjectName("cardTitle")
        self.diagnostic_label = QLabel()
        self.diagnostic_label.setWordWrap(True)
        self.diagnostic_label.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        diagnostics_layout.addWidget(self.diagnostic_label)
        self.clear_logs_button = QPushButton()
        diagnostics_layout.addWidget(self.clear_logs_button)
        self._diag_note = QLabel()
        self._diag_note.setObjectName("inlineNotice")
        self._diag_note.setWordWrap(True)
        diagnostics_layout.addWidget(self._diag_note)
        layout.addWidget(diagnostics)
        layout.addStretch()

        self.clear_logs_button.clicked.connect(self.clear_logs_requested)
        self.open_data_button.clicked.connect(lambda: self.open_path_requested.emit("data"))
        self.open_logs_button.clicked.connect(lambda: self.open_path_requested.emit("logs"))
        self.open_cache_button.clicked.connect(lambda: self.open_path_requested.emit("cache"))

        self._location_str = ""
        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _retranslate(self) -> None:
        self._data_card_title.setText(self._i18n.tr("data.data_card"))
        self.open_data_button.setText(self._i18n.tr("data.open_data"))
        self.open_logs_button.setText(self._i18n.tr("data.open_logs"))
        self.open_cache_button.setText(self._i18n.tr("data.open_cache"))
        self._diag_card_title.setText(self._i18n.tr("data.diag_card"))
        self.clear_logs_button.setText(self._i18n.tr("data.clear_logs"))
        self._diag_note.setText(self._i18n.tr("data.diag_note"))
        if self._location_str:
            self._refresh_location_display()

    def load_location(self, location: str) -> None:
        self._location_str = location
        self._refresh_location_display()

    def _refresh_location_display(self) -> None:
        config = Path(self._location_str)
        data = config.parent
        self.path_label.setText(
            self._i18n.tr("data.path_info", config=str(config), data=str(data))
        )
        frozen = (
            self._i18n.tr("data.diag_frozen_yes")
            if getattr(sys, "frozen", False)
            else self._i18n.tr("data.diag_frozen_no")
        )
        win_ver = sys.getwindowsversion()
        win_name = "Windows 11" if win_ver.build >= 22000 else "Windows 10"
        diag_text = (
            self._i18n.tr("data.diag_python", version=platform.python_version()) + "\n"
            + self._i18n.tr("data.diag_system", name=win_name, build=str(win_ver.build)) + "\n"
            + self._i18n.tr("data.diag_frozen", frozen=frozen)
        )
        self.diagnostic_label.setText(diag_text)
        self._paths = {
            "data": data,
            "logs": data / "logs",
            "cache": data / "cache",
        }

    def path_for(self, key: str) -> str | None:
        path = getattr(self, "_paths", {}).get(key)
        return str(path) if path is not None else None
