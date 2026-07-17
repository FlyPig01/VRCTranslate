from __future__ import annotations

from copy import deepcopy

from PySide6.QtWidgets import (
    QCheckBox,
    QLabel,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import TranslationProfile, TranslationSettings
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.options import formats, languages
from vrctranslate.presentation.qt.pages.settings.common import card, form_layout, scroll_page
from vrctranslate.presentation.qt.widgets import NoWheelComboBox

from .constants import OVERFLOW_POLICIES
from .helpers import set_combo


class RoutesTab(QWidget):
    """Edits the independent OSC and OCR translation routes."""

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._profiles: list[TranslationProfile] = []
        self._build_ui()
        self.retranslate()

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        scroll, _, layout = scroll_page()
        root.addWidget(scroll)

        osc_card, osc_layout = card("")
        self._self_card_title = osc_layout.itemAt(0).widget()
        self._self_card_title.setObjectName("cardTitle")
        osc_form = form_layout()
        self.self_profile_combo = NoWheelComboBox()
        self.self_source_combo = NoWheelComboBox()
        self.self_target_combo = NoWheelComboBox()
        self.format_combo = NoWheelComboBox()
        self.overflow_combo = NoWheelComboBox()
        self._self_profile_label = QLabel()
        self._self_source_label = QLabel()
        self._self_target_label = QLabel()
        self._format_label = QLabel()
        self._overflow_label = QLabel()
        osc_form.addRow(self._self_profile_label, self.self_profile_combo)
        osc_form.addRow(self._self_source_label, self.self_source_combo)
        osc_form.addRow(self._self_target_label, self.self_target_combo)
        osc_form.addRow(self._format_label, self.format_combo)
        osc_form.addRow(self._overflow_label, self.overflow_combo)
        self.self_romaji_check = QCheckBox()
        osc_form.addRow("", self.self_romaji_check)
        osc_layout.addLayout(osc_form)
        layout.addWidget(osc_card)

        ocr_card, ocr_layout = card("")
        self._ocr_card_title = ocr_layout.itemAt(0).widget()
        self._ocr_card_title.setObjectName("cardTitle")
        ocr_form = form_layout()
        self.ocr_profile_combo = NoWheelComboBox()
        self.ocr_source_combo = NoWheelComboBox()
        self.ocr_target_combo = NoWheelComboBox()
        self._ocr_profile_label = QLabel()
        self._ocr_source_label = QLabel()
        self._ocr_target_label = QLabel()
        ocr_form.addRow(self._ocr_profile_label, self.ocr_profile_combo)
        ocr_form.addRow(self._ocr_source_label, self.ocr_source_combo)
        ocr_form.addRow(self._ocr_target_label, self.ocr_target_combo)
        self.ocr_romaji_check = QCheckBox()
        ocr_form.addRow("", self.ocr_romaji_check)
        ocr_layout.addLayout(ocr_form)
        self.ocr_route_warning = QLabel()
        self.ocr_route_warning.setWordWrap(True)
        self.ocr_route_warning.setObjectName("warningNotice")
        ocr_layout.addWidget(self.ocr_route_warning)
        layout.addWidget(ocr_card)
        layout.addStretch()

        self.ocr_profile_combo.currentIndexChanged.connect(self.update_warnings)
        for combo in (
            self.self_profile_combo,
            self.self_source_combo,
            self.self_target_combo,
            self.ocr_profile_combo,
            self.ocr_source_combo,
            self.ocr_target_combo,
        ):
            combo.currentIndexChanged.connect(self.update_warnings)

    def retranslate(self) -> None:
        self._self_card_title.setText(self._i18n.tr("route.self_card"))
        self._ocr_card_title.setText(self._i18n.tr("route.ocr_card"))
        self._self_profile_label.setText(self._i18n.tr("route.profile"))
        self._ocr_profile_label.setText(self._i18n.tr("route.profile"))
        self._self_source_label.setText(self._i18n.tr("route.source"))
        self._self_target_label.setText(self._i18n.tr("route.target"))
        self._ocr_source_label.setText(self._i18n.tr("route.source"))
        self._ocr_target_label.setText(self._i18n.tr("route.target"))
        self._format_label.setText(self._i18n.tr("route.format"))
        self._overflow_label.setText(self._i18n.tr("route.overflow"))
        self.self_romaji_check.setText(self._i18n.tr("route.romaji"))
        self.ocr_romaji_check.setText(self._i18n.tr("route.romaji"))
        self._rebuild_languages()
        self._rebuild_formats()
        self._rebuild_overflow()
        self._refresh_profile_labels()
        self.update_warnings()

    def load_settings(self, settings: TranslationSettings) -> None:
        self._profiles = deepcopy(settings.profiles)
        self._populate_profile_combos(
            settings.self_route.profile_id,
            settings.ocr_route.profile_id,
        )
        set_combo(self.self_source_combo, settings.self_route.source_language)
        set_combo(self.self_target_combo, settings.self_route.target_language)
        set_combo(self.ocr_source_combo, settings.ocr_route.source_language)
        set_combo(self.ocr_target_combo, settings.ocr_route.target_language)
        set_combo(self.format_combo, settings.self_route.message_format)
        set_combo(self.overflow_combo, settings.self_route.overflow_policy)
        self.self_romaji_check.setChecked(settings.self_route.romaji_to_kana)
        self.ocr_romaji_check.setChecked(settings.ocr_route.romaji_to_kana)
        self.update_warnings()

    def collect_settings(self, settings: TranslationSettings) -> None:
        settings.self_route.profile_id = str(self.self_profile_combo.currentData())
        settings.ocr_route.profile_id = str(self.ocr_profile_combo.currentData())
        settings.self_route.source_language = str(self.self_source_combo.currentData())
        settings.self_route.target_language = str(self.self_target_combo.currentData())
        settings.ocr_route.source_language = str(self.ocr_source_combo.currentData())
        settings.ocr_route.target_language = str(self.ocr_target_combo.currentData())
        settings.self_route.message_format = str(self.format_combo.currentData())
        settings.self_route.overflow_policy = str(self.overflow_combo.currentData())
        settings.self_route.romaji_to_kana = self.self_romaji_check.isChecked()
        settings.ocr_route.romaji_to_kana = self.ocr_romaji_check.isChecked()

    def set_profiles(
        self,
        profiles: list[TranslationProfile],
        *,
        preserve: bool = True,
    ) -> None:
        self_id = str(self.self_profile_combo.currentData() or "")
        ocr_id = str(self.ocr_profile_combo.currentData() or "")
        self._profiles = deepcopy(profiles)
        if not preserve or self_id not in {p.id for p in profiles}:
            self_id = profiles[0].id if profiles else ""
        if not preserve or ocr_id not in {p.id for p in profiles}:
            ocr_id = profiles[0].id if profiles else ""
        self._populate_profile_combos(self_id, ocr_id)
        self.update_warnings()

    def _populate_profile_combos(self, self_id: str, ocr_id: str) -> None:
        for combo, selected in (
            (self.self_profile_combo, self_id),
            (self.ocr_profile_combo, ocr_id),
        ):
            combo.blockSignals(True)
            combo.clear()
            for profile in self._profiles:
                combo.addItem(profile.name, profile.id)
            set_combo(combo, selected)
            combo.blockSignals(False)

    def _refresh_profile_labels(self) -> None:
        by_id = {profile.id: profile.name for profile in self._profiles}
        for combo in (self.self_profile_combo, self.ocr_profile_combo):
            combo.blockSignals(True)
            for index in range(combo.count()):
                profile_id = str(combo.itemData(index) or "")
                if profile_id in by_id:
                    combo.setItemText(index, by_id[profile_id])
            combo.blockSignals(False)

    def _rebuild_languages(self) -> None:
        for combo, include_auto in (
            (self.self_source_combo, True),
            (self.self_target_combo, False),
            (self.ocr_source_combo, True),
            (self.ocr_target_combo, False),
        ):
            current = str(combo.currentData() or "")
            combo.blockSignals(True)
            combo.clear()
            for label, value in languages(self._i18n):
                if include_auto or value != "auto":
                    combo.addItem(label, value)
            set_combo(combo, current)
            combo.blockSignals(False)

    def _rebuild_formats(self) -> None:
        current = str(self.format_combo.currentData() or "")
        self.format_combo.clear()
        for label, value in formats(self._i18n):
            self.format_combo.addItem(label, value)
        set_combo(self.format_combo, current)

    def _rebuild_overflow(self) -> None:
        current = str(self.overflow_combo.currentData() or "")
        self.overflow_combo.clear()
        for key, value in OVERFLOW_POLICIES:
            self.overflow_combo.addItem(self._i18n.tr(key), value)
        set_combo(self.overflow_combo, current)

    def update_warnings(self) -> None:
        self._update_ocr_warning()

    def _profile_provider(self, profile_id: str) -> str:
        for profile in self._profiles:
            if profile.id == profile_id:
                return profile.provider
        return ""

    def _update_ocr_warning(self) -> None:
        provider = self._profile_provider(
            str(self.ocr_profile_combo.currentData() or "")
        )
        self.ocr_route_warning.setText(
            self._i18n.tr("route.ocr_warning_llm")
            if provider == "openai_compatible"
            else self._i18n.tr("route.ocr_warning_default")
        )
