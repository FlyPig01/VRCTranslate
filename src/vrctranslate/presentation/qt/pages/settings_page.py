from __future__ import annotations

from copy import deepcopy

from PySide6.QtCore import QTimer, Signal
from PySide6.QtGui import QKeySequence, QShortcut
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPushButton,
    QStackedWidget,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings, TranslationProfile
from vrctranslate.application.ports.local_models import LocalTranslationModel
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.pages.settings import (
    DataDiagnosticsPage,
    OcrSettingsPage,
    OscSettingsPage,
    TranslationSettingsPage,
)
from vrctranslate.presentation.qt.widgets.settings_section_nav import SettingsSectionNav
from vrctranslate.presentation.qt.view_models.settings_draft import SettingsDraft


class SettingsPage(QWidget):
    """Fixed settings shell around four independently scrollable sections."""

    save_requested = Signal()
    test_translation_requested = Signal()
    argos_refresh_requested = Signal()
    argos_catalog_requested = Signal()
    argos_install_requested = Signal(str, str, str)
    argos_pivot_install_requested = Signal(list)
    argos_remove_requested = Signal(str, str)
    open_models_requested = Signal()
    clear_logs_requested = Signal()
    open_path_requested = Signal(str)
    capture_test_requested = Signal(str)
    discard_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._draft = SettingsDraft()
        self._loading = False
        self._dirty = False
        self._build_ui()
        self._retranslate()
        self._forward_signals()
        self._connect_dirty_tracking()
        i18n.language_changed.connect(lambda _: self._retranslate())

    @property
    def has_unsaved_changes(self) -> bool:
        return self._draft.dirty

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(22, 18, 22, 14)
        root.setSpacing(12)

        header = QHBoxLayout()
        header.setSpacing(16)
        title_column = QVBoxLayout()
        title_column.setSpacing(2)
        self._title = QLabel()
        self._title.setObjectName("pageTitle")
        self._subtitle = QLabel()
        self._subtitle.setObjectName("pageSubtitle")
        title_column.addWidget(self._title)
        title_column.addWidget(self._subtitle)
        header.addLayout(title_column)
        header.addStretch()
        self._dirty_label = QLabel()
        self._dirty_label.setObjectName("settingsSaveState")
        self._save_button = QPushButton(icon=load_icon("ui/action_save.svg"), text="")
        self._save_button.setObjectName("primaryButton")
        header.addWidget(self._dirty_label)
        header.addWidget(self._save_button)
        root.addLayout(header)

        lang_row = QHBoxLayout()
        lang_row.setSpacing(8)
        self._lang_label = QLabel()
        self._lang_btn = QPushButton()
        self._lang_btn.setObjectName("languageToggle")
        self._lang_btn.setFlat(True)
        self._lang_btn.clicked.connect(self._cycle_language)
        self._lang_locales = ["zh_CN", "en_US", "ja_JP"]
        self._lang_index = 0
        lang_row.addWidget(self._lang_label)
        lang_row.addWidget(self._lang_btn)
        lang_row.addStretch()
        root.addLayout(lang_row)

        self.section_nav = SettingsSectionNav(self._i18n)
        root.addWidget(self.section_nav)
        self.section_stack = QStackedWidget()
        self.translation_page = TranslationSettingsPage(self._i18n)
        self.osc_page = OscSettingsPage(self._i18n)
        self.ocr_page = OcrSettingsPage(self._i18n)
        self.data_page = DataDiagnosticsPage(self._i18n)
        for page in (
            self.translation_page,
            self.osc_page,
            self.ocr_page,
            self.data_page,
        ):
            self.section_stack.addWidget(page)
        root.addWidget(self.section_stack, 1)

        footer = QHBoxLayout()
        footer.setSpacing(16)
        self._location_summary = QLabel()
        self._location_summary.setObjectName("pageSubtitle")
        footer.addWidget(self._location_summary)
        footer.addStretch()
        self._shortcut_hint = QLabel()
        self._shortcut_hint.setStyleSheet("color: #8d99a8; font-size: 12px;")
        footer.addWidget(self._shortcut_hint)
        root.addLayout(footer)

        self.section_nav.section_changed.connect(self.section_stack.setCurrentIndex)
        self._save_button.clicked.connect(self.save_requested)
        self._save_shortcut = QShortcut(QKeySequence.StandardKey.Save, self)
        self._save_shortcut.activated.connect(self.save_requested)

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._title.setText(t("page.settings.title"))
        self._subtitle.setText(t("page.settings.subtitle"))
        self._lang_label.setText(t("page.settings.ui_language"))
        self._save_button.setText(t("page.settings.save_button"))
        self._shortcut_hint.setText(t("page.settings.shortcut_hint"))
        if self._dirty:
            self._dirty_label.setText(t("page.settings.dirty"))
        else:
            self._dirty_label.setText(t("page.settings.saved"))
        self._update_lang_button()

    def _update_lang_button(self) -> None:
        locale_map = {"zh_CN": "中文", "en_US": "English", "ja_JP": "日本語"}
        locale = self._lang_locales[self._lang_index]
        self._lang_btn.setText(locale_map.get(locale, locale))

    def _cycle_language(self) -> None:
        self._lang_index = (self._lang_index + 1) % len(self._lang_locales)
        self._update_lang_button()
        self.save_requested.emit()

    def _forward_signals(self) -> None:
        page = self.translation_page
        page.test_translation_requested.connect(self.test_translation_requested)
        page.argos_refresh_requested.connect(self.argos_refresh_requested)
        page.argos_catalog_requested.connect(self.argos_catalog_requested)
        page.argos_install_requested.connect(self.argos_install_requested)
        page.argos_pivot_install_requested.connect(self.argos_pivot_install_requested)
        page.argos_remove_requested.connect(self.argos_remove_requested)
        page.open_models_requested.connect(self.open_models_requested)
        self.ocr_page.capture_test_requested.connect(self.capture_test_requested)
        self.data_page.clear_logs_requested.connect(self.clear_logs_requested)
        self.data_page.open_path_requested.connect(self.open_path_requested)

    def _connect_dirty_tracking(self) -> None:
        ignored_combos = {
            self.translation_page.profile_combo,
            self.translation_page.argos_source_filter,
            self.translation_page.argos_target_filter,
            self.translation_page.installed_model_combo,
            self.translation_page.available_model_combo,
        }
        for edit in self.findChildren(QLineEdit):
            edit.textChanged.connect(self._mark_dirty)
        for combo in self.findChildren(QComboBox):
            if combo not in ignored_combos:
                combo.currentIndexChanged.connect(self._mark_dirty)
        for check in self.findChildren(QCheckBox):
            check.checkStateChanged.connect(self._mark_dirty)
        self.translation_page.new_profile_button.clicked.connect(self._mark_dirty)
        self.translation_page.delete_profile_button.clicked.connect(self._mark_dirty)

    def _mark_dirty(self, *_: object) -> None:
        if self._loading:
            return
        self._dirty = True
        self._draft.mark_dirty()
        self._dirty_label.setText(self._i18n.tr("page.settings.dirty"))
        self._dirty_label.setProperty("dirty", True)
        self._save_button.setEnabled(True)

    def mark_saved(self) -> None:
        self._dirty = False
        self._draft.mark_saved()
        self._dirty_label.setText(self._i18n.tr("page.settings.just_saved"))
        self._dirty_label.setProperty("dirty", False)
        self._save_button.setEnabled(False)
        timer = QTimer(self)
        timer.setSingleShot(True)
        timer.setInterval(1800)
        timer.timeout.connect(self._reset_saved_label)
        timer.start()

    def _reset_saved_label(self) -> None:
        if not self._dirty:
            self._dirty_label.setText(self._i18n.tr("page.settings.saved"))

    def load_settings(
        self,
        settings: AppSettings,
        location: str,
        argos_available: bool,
        model_directory: str,
    ) -> None:
        self._loading = True
        try:
            self._draft.load(settings)
            self.translation_page.load_settings(
                settings, argos_available, model_directory
            )
            self.osc_page.load_settings(settings)
            self.ocr_page.load_settings(settings)
            self.data_page.load_location(location)
            self._location_summary.setText(
                self._i18n.tr("page.settings.config_location", path=location)
            )
            lang_index = (self._lang_locales.index(settings.ui.language)
                          if settings.ui.language in self._lang_locales
                          else 0)
            self._lang_index = lang_index
            self._update_lang_button()
        finally:
            self._loading = False
        self.mark_saved()

    def collect_settings(self, current: AppSettings) -> AppSettings:
        settings = deepcopy(self._draft.settings)
        for name in (
            "input_x", "input_y", "input_width",
            "ocr_overlay_x", "ocr_overlay_y", "ocr_overlay_width", "ocr_overlay_height",
            "main_width", "main_height", "main_x", "main_y",
        ):
            setattr(settings.ui, name, getattr(current.ui, name))
        for name in (
            "region_x", "region_y", "region_width", "region_height", "window_title",
        ):
            setattr(settings.ocr, name, getattr(current.ocr, name))
        settings.ui.language = self._lang_locales[self._lang_index]
        self.translation_page.collect_settings(settings)
        self.osc_page.collect_settings(settings)
        self.ocr_page.collect_settings(settings)
        self._draft.replace(settings)
        return settings

    def selected_profile(self) -> TranslationProfile:
        return self.translation_page.selected_profile()

    def set_test_status(self, message: str, failed: bool = False) -> None:
        self.translation_page.set_test_status(message, failed)

    def set_model_catalog(
        self,
        state: str,
        installed: list[LocalTranslationModel],
        available: list[LocalTranslationModel],
        usage: int,
        message: str = "",
    ) -> None:
        self.translation_page.set_model_catalog(
            state, installed, available, usage, message
        )

    def set_model_state(
        self,
        installed: list[LocalTranslationModel],
        available: list[LocalTranslationModel],
        usage: int,
    ) -> None:
        state = "ready" if available else "empty"
        self.set_model_catalog(state, installed, available, usage)

    def set_argos_status(self, message: str) -> None:
        self.translation_page.set_argos_status(message)

    def set_argos_progress(self, downloaded: int, total: int) -> None:
        self.translation_page.set_argos_progress(downloaded, total)

    def hide_argos_progress(self) -> None:
        self.translation_page.hide_argos_progress()

    def set_capture_status(self, message: str) -> None:
        self.ocr_page.set_capture_status(message)

    def set_capture_preview(self, pixels: object | None, message: str) -> None:
        self.ocr_page.set_capture_preview(pixels, message)

    def path_for(self, key: str) -> str | None:
        return self.data_page.path_for(key)

    def confirm_leave(self) -> bool:
        if not self._dirty:
            return True
        t = self._i18n.tr
        box = QMessageBox(self)
        box.setWindowTitle(t("page.settings.leave_title"))
        box.setText(t("page.settings.leave_text"))
        box.setInformativeText(t("page.settings.leave_info"))
        save = box.addButton(t("page.settings.leave_save"), QMessageBox.ButtonRole.AcceptRole)
        discard = box.addButton(t("page.settings.leave_discard"), QMessageBox.ButtonRole.DestructiveRole)
        box.addButton(t("page.settings.leave_cancel"), QMessageBox.ButtonRole.RejectRole)
        box.exec()
        clicked = box.clickedButton()
        if clicked is save:
            self.save_requested.emit()
            return not self._dirty
        if clicked is discard:
            self._dirty = False
            self._draft.mark_saved()
            self.discard_requested.emit()
            return True
        return False
