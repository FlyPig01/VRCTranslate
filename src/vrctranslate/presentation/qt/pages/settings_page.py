from __future__ import annotations

from copy import deepcopy

from PySide6.QtCore import QTimer, Signal
from PySide6.QtGui import QKeySequence, QShortcut
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QGridLayout,
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
    clear_logs_requested = Signal()
    open_path_requested = Signal(str)
    capture_test_requested = Signal(str)
    ocr_model_install_requested = Signal(str)
    ocr_model_remove_requested = Signal(str)
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

        header = QGridLayout()
        header.setHorizontalSpacing(16)
        header.setVerticalSpacing(4)
        self._title = QLabel()
        self._title.setObjectName("pageTitle")
        self._subtitle = QLabel()
        self._subtitle.setObjectName("pageSubtitle")
        self._subtitle.setWordWrap(True)
        header.addWidget(self._title, 0, 0)
        header.addWidget(self._subtitle, 1, 0, 1, 2)
        actions = QHBoxLayout()
        actions.setSpacing(8)
        self._lang_label = QLabel()
        self._lang_btn = QPushButton()
        self._lang_btn.setObjectName("languageToggle")
        self._lang_btn.setFlat(True)
        self._lang_btn.clicked.connect(self._cycle_language)
        self._lang_locales = ["zh_CN", "en_US", "ja_JP"]
        self._lang_index = 0
        self._dirty_label = QLabel()
        self._dirty_label.setObjectName("settingsSaveState")
        self._save_button = QPushButton(icon=load_icon("ui/action_save.svg"), text="")
        self._save_button.setObjectName("primaryButton")
        self._discard_button = QPushButton()
        self._discard_button.setObjectName("secondaryButton")
        actions.addWidget(self._lang_label)
        actions.addWidget(self._lang_btn)
        actions.addWidget(self._dirty_label)
        actions.addWidget(self._discard_button)
        actions.addWidget(self._save_button)
        header.addLayout(actions, 0, 1)
        header.setColumnStretch(0, 1)
        root.addLayout(header)

        self.section_nav = SettingsSectionNav(self._i18n)
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
        content_row = QHBoxLayout()
        content_row.setSpacing(12)
        content_row.addWidget(self.section_nav)
        content_row.addWidget(self.section_stack, 1)
        root.addLayout(content_row, 1)

        footer = QHBoxLayout()
        footer.setSpacing(16)
        self._location_summary = QLabel()
        self._location_summary.setObjectName("pageSubtitle")
        self._location_summary.setWordWrap(True)
        footer.addWidget(self._location_summary)
        footer.addStretch()
        self._shortcut_hint = QLabel()
        self._shortcut_hint.setStyleSheet("color: #8d99a8; font-size: 12px;")
        footer.addWidget(self._shortcut_hint)
        root.addLayout(footer)

        self.section_nav.section_changed.connect(self.section_stack.setCurrentIndex)
        self._save_button.clicked.connect(self.save_requested)
        self._discard_button.clicked.connect(self.discard_requested)
        self._save_shortcut = QShortcut(QKeySequence.StandardKey.Save, self)
        self._save_shortcut.activated.connect(self.save_requested)

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._title.setText(t("page.settings.title"))
        self._subtitle.setText(t("page.settings.subtitle"))
        self._lang_label.setText(t("page.settings.ui_language"))
        self._save_button.setText(t("page.settings.save_button"))
        self._discard_button.setText(t("save_state.discard"))
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
        self.ocr_page.capture_test_requested.connect(self.capture_test_requested)
        self.ocr_page.model_install_requested.connect(self.ocr_model_install_requested)
        self.ocr_page.model_remove_requested.connect(self.ocr_model_remove_requested)
        self.data_page.clear_logs_requested.connect(self.clear_logs_requested)
        self.data_page.open_path_requested.connect(self.open_path_requested)

    def _connect_dirty_tracking(self) -> None:
        ignored_combos = {
            self.translation_page.profile_combo,
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
        self._discard_button.setEnabled(True)

    def mark_saved(self) -> None:
        self._dirty = False
        self._draft.mark_saved()
        self._dirty_label.setText(self._i18n.tr("page.settings.just_saved"))
        self._dirty_label.setProperty("dirty", False)
        self._save_button.setEnabled(False)
        self._discard_button.setEnabled(False)
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
    ) -> None:
        self._loading = True
        try:
            self._draft.load(settings)
            self.translation_page.load_settings(settings)
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
        # Start from the newest persisted object so settings owned by the
        # quick-input and OCR feature pages can never be rolled back here.
        settings = deepcopy(current)
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

    def set_capture_status(self, message: str) -> None:
        self.ocr_page.set_capture_status(message)

    def set_capture_preview(self, pixels: object | None, message: str) -> None:
        self.ocr_page.set_capture_preview(pixels, message)

    def set_ocr_model_status(
        self,
        language: str,
        installed: bool,
        version: str,
        installed_size: int,
        *,
        busy: bool = False,
        error: str = "",
    ) -> None:
        self.ocr_page.set_model_status(
            language,
            installed,
            version,
            installed_size,
            busy=busy,
            error=error,
        )

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
