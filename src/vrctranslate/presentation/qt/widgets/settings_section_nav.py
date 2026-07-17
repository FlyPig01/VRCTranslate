from __future__ import annotations

from PySide6.QtCore import Signal
from PySide6.QtWidgets import QTabBar

from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon


class SettingsSectionNav(QTabBar):
    section_changed = Signal(int)

    def __init__(self, i18n: I18nManager, parent=None) -> None:
        super().__init__(parent)
        self._i18n = i18n
        self.setObjectName("settingsSectionNav")
        self.setExpanding(True)
        self._section_keys = (
            "settings.section.translation",
            "settings.section.osc",
            "settings.section.ocr",
            "settings.section.data",
        )
        self._section_icons = (
            "ui/settings_translation.svg",
            "ui/settings_osc.svg",
            "ui/settings_ocr.svg",
            "ui/settings_data.svg",
        )
        self._retranslate()
        self.currentChanged.connect(self.section_changed)
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _retranslate(self) -> None:
        for i, key in enumerate(self._section_keys):
            if i < self.count():
                self.setTabText(i, self._i18n.tr(key))
                self.setTabIcon(i, load_icon(self._section_icons[i]))
            else:
                self.addTab(load_icon(self._section_icons[i]), self._i18n.tr(key))
