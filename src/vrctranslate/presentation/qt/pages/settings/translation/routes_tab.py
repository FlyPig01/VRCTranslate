from __future__ import annotations

from copy import deepcopy

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QCheckBox, QLabel, QVBoxLayout, QWidget

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
        self._global_glossary_enabled = True
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
        osc_form.setLabelAlignment(
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
        )
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
        self._self_romaji_label = QLabel()
        for label in (
            self._self_profile_label,
            self._self_source_label,
            self._self_target_label,
            self._format_label,
            self._overflow_label,
            self._self_romaji_label,
        ):
            label.setAlignment(
                Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
            )
        osc_form.addRow(self._self_profile_label, self.self_profile_combo)
        osc_form.addRow(self._self_source_label, self.self_source_combo)
        osc_form.addRow(self._self_target_label, self.self_target_combo)
        osc_form.addRow(self._format_label, self.format_combo)
        osc_form.addRow(self._overflow_label, self.overflow_combo)
        self.self_romaji_combo = NoWheelComboBox()
        osc_form.addRow(self._self_romaji_label, self.self_romaji_combo)
        self.self_glossary_enabled = QCheckBox()
        osc_form.addRow("", self.self_glossary_enabled)
        self.self_glossary_status = QLabel()
        self.self_glossary_status.setObjectName("fieldHint")
        self.self_glossary_status.setWordWrap(True)
        osc_form.addRow("", self.self_glossary_status)
        self.self_romaji_help = QLabel()
        self.self_romaji_help.setWordWrap(True)
        self.self_romaji_help.setObjectName("fieldHint")
        osc_form.addRow("", self.self_romaji_help)
        osc_layout.addLayout(osc_form)
        layout.addWidget(osc_card)

        ocr_card, ocr_layout = card("")
        self._ocr_card_title = ocr_layout.itemAt(0).widget()
        self._ocr_card_title.setObjectName("cardTitle")
        ocr_form = form_layout()
        ocr_form.setLabelAlignment(
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
        )
        self.ocr_profile_combo = NoWheelComboBox()
        self.ocr_source_combo = NoWheelComboBox()
        self.ocr_target_combo = NoWheelComboBox()
        self._ocr_profile_label = QLabel()
        self._ocr_source_label = QLabel()
        self._ocr_target_label = QLabel()
        self._ocr_romaji_label = QLabel()
        for label in (
            self._ocr_profile_label,
            self._ocr_source_label,
            self._ocr_target_label,
            self._ocr_romaji_label,
        ):
            label.setAlignment(
                Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
            )
        ocr_form.addRow(self._ocr_profile_label, self.ocr_profile_combo)
        ocr_form.addRow(self._ocr_source_label, self.ocr_source_combo)
        ocr_form.addRow(self._ocr_target_label, self.ocr_target_combo)
        self.ocr_romaji_combo = NoWheelComboBox()
        ocr_form.addRow(self._ocr_romaji_label, self.ocr_romaji_combo)
        self.ocr_glossary_enabled = QCheckBox()
        ocr_form.addRow("", self.ocr_glossary_enabled)
        self.ocr_glossary_status = QLabel()
        self.ocr_glossary_status.setObjectName("fieldHint")
        self.ocr_glossary_status.setWordWrap(True)
        ocr_form.addRow("", self.ocr_glossary_status)
        self.ocr_romaji_help = QLabel()
        self.ocr_romaji_help.setWordWrap(True)
        self.ocr_romaji_help.setObjectName("fieldHint")
        ocr_form.addRow("", self.ocr_romaji_help)
        self.ocr_route_warning = QLabel()
        self.ocr_route_warning.setWordWrap(True)
        self.ocr_route_warning.setObjectName("warningNotice")
        ocr_form.addRow("", self.ocr_route_warning)
        ocr_layout.addLayout(ocr_form)
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
            self.self_romaji_combo,
            self.ocr_romaji_combo,
        ):
            combo.currentIndexChanged.connect(self.update_warnings)
        self.self_glossary_enabled.toggled.connect(self.update_warnings)
        self.ocr_glossary_enabled.toggled.connect(self.update_warnings)

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
        self._self_romaji_label.setText(self._i18n.tr("route.romaji_mode"))
        self._ocr_romaji_label.setText(self._i18n.tr("route.romaji_mode"))
        self.self_glossary_enabled.setText(self._i18n.tr("route.glossary_enabled"))
        self.ocr_glossary_enabled.setText(self._i18n.tr("route.glossary_enabled"))
        self._rebuild_languages()
        self._rebuild_formats()
        self._rebuild_overflow()
        self._rebuild_romaji_modes()
        self._refresh_profile_labels()
        self.update_warnings()

    def load_settings(
        self,
        settings: TranslationSettings,
        global_glossary_enabled: bool = True,
    ) -> None:
        self._profiles = deepcopy(settings.profiles)
        self._global_glossary_enabled = global_glossary_enabled
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
        set_combo(self.self_romaji_combo, settings.self_route.romaji_mode)
        set_combo(self.ocr_romaji_combo, settings.ocr_route.romaji_mode)
        self.self_glossary_enabled.setChecked(settings.self_route.glossary_enabled)
        self.ocr_glossary_enabled.setChecked(settings.ocr_route.glossary_enabled)
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
        settings.self_route.romaji_mode = str(
            self.self_romaji_combo.currentData() or "auto"
        )
        settings.ocr_route.romaji_mode = str(
            self.ocr_romaji_combo.currentData() or "off"
        )
        settings.self_route.glossary_enabled = self.self_glossary_enabled.isChecked()
        settings.ocr_route.glossary_enabled = self.ocr_glossary_enabled.isChecked()

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

    def set_glossary_global_enabled(self, enabled: bool) -> None:
        self._global_glossary_enabled = enabled
        self.update_warnings()

    def _populate_profile_combos(self, self_id: str, ocr_id: str) -> None:
        for combo, selected, profiles in (
            (
                self.self_profile_combo,
                self_id,
                [
                    profile for profile in self._profiles
                    if profile.provider != "multimodal_openai"
                ],
            ),
            (self.ocr_profile_combo, ocr_id, self._profiles),
        ):
            combo.blockSignals(True)
            combo.clear()
            for profile in profiles:
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
        current = str(self.ocr_source_combo.currentData() or "")
        self.ocr_source_combo.blockSignals(True)
        self.ocr_source_combo.clear()
        for label, value in languages(self._i18n):
            if value in {"zh-CN", "ja", "en"}:
                self.ocr_source_combo.addItem(label, value)
        set_combo(
            self.ocr_source_combo,
            current if current in {"zh-CN", "ja", "en"} else "ja",
        )
        self.ocr_source_combo.blockSignals(False)

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

    def _rebuild_romaji_modes(self) -> None:
        for combo, fallback in (
            (self.self_romaji_combo, "auto"),
            (self.ocr_romaji_combo, "off"),
        ):
            current = str(combo.currentData() or fallback)
            combo.blockSignals(True)
            combo.clear()
            for mode in ("off", "auto", "force"):
                combo.addItem(self._i18n.tr(f"route.romaji_{mode}"), mode)
            set_combo(combo, current)
            combo.blockSignals(False)

    def update_warnings(self) -> None:
        self._update_ocr_warning()
        self._update_romaji_help()
        self._update_glossary_status()

    def _update_glossary_status(self) -> None:
        for profile_combo, enabled, label in (
            (
                self.self_profile_combo,
                self.self_glossary_enabled,
                self.self_glossary_status,
            ),
            (
                self.ocr_profile_combo,
                self.ocr_glossary_enabled,
                self.ocr_glossary_status,
            ),
        ):
            profile_id = str(profile_combo.currentData() or "")
            provider = self._profile_provider(profile_id)
            profile_name = next(
                (profile.name for profile in self._profiles if profile.id == profile_id),
                "",
            )
            if not self._global_glossary_enabled:
                key = "route.glossary_status_global_disabled"
            elif not enabled.isChecked():
                key = "route.glossary_status_disabled"
            elif provider in {"openai_compatible", "multimodal_openai"}:
                key = "route.glossary_status_prompt"
            elif provider == "test" or not provider:
                key = "route.glossary_status_none"
            else:
                key = "route.glossary_status_local"
            label.setText(self._i18n.tr(key, profile=profile_name))

    def _update_romaji_help(self) -> None:
        for source_combo, mode_combo, help_label, visual_route in (
            (
                self.self_source_combo,
                self.self_romaji_combo,
                self.self_romaji_help,
                False,
            ),
            (
                self.ocr_source_combo,
                self.ocr_romaji_combo,
                self.ocr_romaji_help,
                self._profile_provider(
                    str(self.ocr_profile_combo.currentData() or "")
                )
                == "multimodal_openai",
            ),
        ):
            source = str(source_combo.currentData() or "")
            available = source in {"auto", "ja"} and not visual_route
            mode_combo.setEnabled(available)
            if visual_route:
                help_label.setText(
                    self._i18n.tr("route.romaji_help_multimodal")
                )
            elif available:
                mode = str(mode_combo.currentData() or "off")
                help_label.setText(self._i18n.tr(f"route.romaji_help_{mode}"))
            else:
                help_label.setText(self._i18n.tr("route.romaji_help_unavailable"))

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
            self._i18n.tr(
                "route.ocr_warning_multimodal"
                if provider == "multimodal_openai"
                else "route.ocr_warning_llm"
                if provider == "openai_compatible"
                else "route.ocr_warning_default"
            )
        )
