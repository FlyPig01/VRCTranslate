from __future__ import annotations

from PySide6.QtCore import QSize, Signal
from PySide6.QtWidgets import QListWidget, QListWidgetItem

from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon


class SettingsSectionNav(QListWidget):
    """Compact vertical secondary navigation for settings."""

    section_changed = Signal(int)

    def __init__(self, i18n: I18nManager, parent=None) -> None:
        super().__init__(parent)
        self._i18n = i18n
        self.setObjectName("settingsSectionNav")
        self.setFixedWidth(176)
        self.setIconSize(QSize(20, 20))
        self.setSpacing(3)
        self._section_keys = (
            "settings.section.translation",
            "settings.section.osc",
            "settings.section.ocr",
            "settings.section.voice",
            "settings.section.data",
        )
        self._section_icons = (
            "ui/settings_translation.svg",
            "ui/settings_osc.svg",
            "ui/settings_ocr.svg",
            "ui/nav_voice.svg",
            "ui/settings_data.svg",
        )
        for icon in self._section_icons:
            item = QListWidgetItem(load_icon(icon), "")
            item.setSizeHint(QSize(0, 42))
            self.addItem(item)
        self._retranslate()
        self.currentRowChanged.connect(self.section_changed)
        self.setCurrentRow(0)
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _retranslate(self) -> None:
        for index, key in enumerate(self._section_keys):
            item = self.item(index)
            if item is not None:
                item.setText(self._i18n.tr(key))

    # Compatibility helpers retained for existing lightweight tests.
    def tabText(self, index: int) -> str:
        item = self.item(index)
        return item.text() if item is not None else ""
