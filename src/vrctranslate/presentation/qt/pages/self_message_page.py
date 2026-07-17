from __future__ import annotations

from PySide6.QtCore import Signal
from PySide6.QtWidgets import QFrame, QLabel, QPushButton, QVBoxLayout, QWidget

from vrctranslate.presentation.qt.i18n import I18nManager


class SelfMessagePage(QWidget):
    show_input_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._profile_name = ""
        self._build_ui()
        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _build_ui(self) -> None:
        layout = QVBoxLayout(self)
        layout.setContentsMargins(22, 22, 22, 22)
        layout.setSpacing(14)
        self._title = QLabel()
        self._title.setObjectName("pageTitle")
        self._subtitle = QLabel()
        self._subtitle.setObjectName("pageSubtitle")
        self._subtitle.setWordWrap(True)
        card = QFrame()
        card.setObjectName("card")
        card_layout = QVBoxLayout(card)
        card_layout.setContentsMargins(20, 18, 20, 18)
        card_layout.setSpacing(10)
        self._card_title = QLabel()
        self._card_title.setObjectName("cardTitle")
        self._hint = QLabel()
        self._hint.setWordWrap(True)
        self._profile_label = QLabel()
        self._status_label = QLabel()
        self._status_label.setObjectName("statusPill")
        self._preview_card = QFrame()
        self._preview_card.setObjectName("previewCard")
        self._preview_card.setVisible(False)
        preview_layout = QVBoxLayout(self._preview_card)
        preview_layout.setContentsMargins(14, 12, 14, 12)
        preview_layout.setSpacing(6)
        self._last_original_label = QLabel()
        self._last_original_label.setObjectName("previewOriginal")
        self._last_original_label.setWordWrap(True)
        self._last_translated_label = QLabel()
        self._last_translated_label.setObjectName("previewTranslated")
        self._last_translated_label.setWordWrap(True)
        preview_layout.addWidget(self._last_original_label)
        preview_layout.addWidget(self._last_translated_label)
        self._show_button = QPushButton()
        self._show_button.setObjectName("primaryButton")
        self._show_button.clicked.connect(self.show_input_requested)
        card_layout.addWidget(self._card_title)
        card_layout.addWidget(self._hint)
        card_layout.addSpacing(4)
        card_layout.addWidget(self._profile_label)
        card_layout.addWidget(self._status_label)
        card_layout.addSpacing(4)
        card_layout.addWidget(self._preview_card)
        card_layout.addSpacing(4)
        card_layout.addWidget(self._show_button)
        layout.addWidget(self._title)
        layout.addWidget(self._subtitle)
        layout.addWidget(card)
        layout.addStretch()

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._title.setText(t("page.quick_input.title"))
        self._subtitle.setText(t("page.quick_input.subtitle"))
        self._card_title.setText(t("page.quick_input.card_title"))
        self._hint.setText(t("page.quick_input.hint"))
        self._profile_label.setText(t("page.quick_input.profile", name=self._profile_name))
        self._show_button.setText(t("page.quick_input.show_button"))
        if not self._preview_card.isVisible():
            self._status_label.setText(t("page.quick_input.status_waiting"))

    def set_profile(self, name: str) -> None:
        self._profile_name = name
        self._profile_label.setText(self._i18n.tr("page.quick_input.profile", name=name))

    def set_status(self, message: str) -> None:
        self._status_label.setText(message)

    def set_last_translation(self, original: str, translated: str) -> None:
        t = self._i18n.tr
        self._last_original_label.setText(t("page.quick_input.preview_original", text=original))
        self._last_translated_label.setText(t("page.quick_input.preview_translated", text=translated))
        self._preview_card.setVisible(True)
