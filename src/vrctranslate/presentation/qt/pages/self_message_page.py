from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QFormLayout,
    QFrame,
    QGridLayout,
    QLabel,
    QScrollArea,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import UiSettings
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.widgets import NumericLineEdit


class SelfMessagePage(QWidget):
    input_settings_changed = Signal(bool, int)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._profile_name = ""
        self._status_text = ""
        self._last_original = ""
        self._last_translated = ""
        self._loading_settings = False
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
        self._content_layout = QGridLayout(self._content)
        layout = self._content_layout
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
        self.input_topmost_check = QCheckBox()
        self.input_width_edit = NumericLineEdit(320, 1200)
        self._width_label = QLabel()
        form.addRow(self.input_topmost_check)
        form.addRow(self._width_label, self.input_width_edit)
        settings_layout.addLayout(form)
        layout.addWidget(self._settings_card, 1, 0, 1, 2)

        layout.setColumnStretch(0, 1)
        layout.setColumnStretch(1, 1)
        layout.setRowStretch(2, 1)
        self._scroll.setWidget(self._content)
        root.addWidget(self._scroll, 1)
        self._apply_responsive_layout()

        self.input_topmost_check.checkStateChanged.connect(self._settings_edited)
        self.input_width_edit.textChanged.connect(self._settings_edited)

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
        finally:
            self._loading_settings = False

    def collect_ui_settings(self, settings: UiSettings) -> None:
        settings.input_topmost = self.input_topmost_check.isChecked()
        settings.input_width = int(self.input_width_edit.value())

    def _settings_edited(self, *_: object) -> None:
        if self._loading_settings:
            return
        try:
            width = int(self.input_width_edit.value())
        except ValueError:
            return
        self.input_settings_changed.emit(
            self.input_topmost_check.isChecked(), width
        )

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        self._apply_responsive_layout()

    def _apply_responsive_layout(self) -> None:
        viewport_width = self._scroll.viewport().width()
        if viewport_width <= 0:
            viewport_width = max(0, self.width() - 44)
        narrow = viewport_width < 900
        if narrow == self._narrow_layout:
            return
        self._narrow_layout = narrow

        layout = self._content_layout
        for card in (self._status_card, self._recent_card, self._settings_card):
            layout.removeWidget(card)
        if narrow:
            layout.addWidget(self._status_card, 0, 0, 1, 2)
            layout.addWidget(self._recent_card, 1, 0, 1, 2)
            layout.addWidget(self._settings_card, 2, 0, 1, 2)
        else:
            layout.addWidget(self._status_card, 0, 0)
            layout.addWidget(self._recent_card, 0, 1)
            layout.addWidget(self._settings_card, 1, 0, 1, 2)
