from __future__ import annotations

from datetime import datetime

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QAbstractItemView,
    QComboBox,
    QFrame,
    QGridLayout,
    QHeaderView,
    QLabel,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import OcrSettings
from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon


class OcrPage(QWidget):
    refresh_requested = Signal()
    region_selection_requested = Signal()
    toggle_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._running = False
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
        layout.addWidget(self._title)
        layout.addWidget(self._subtitle)

        controls_card = QFrame()
        controls_card.setObjectName("card")
        card_layout = QVBoxLayout(controls_card)
        card_layout.setContentsMargins(20, 18, 20, 18)
        card_layout.setSpacing(12)
        self._privacy = QLabel()
        self._privacy.setWordWrap(True)
        self._privacy.setObjectName("inlineNotice")
        card_layout.addWidget(self._privacy)

        controls = QGridLayout()
        controls.setHorizontalSpacing(12)
        controls.setVerticalSpacing(10)
        self.window_combo = QComboBox()
        self._refresh_button = QPushButton()
        self._region_button = QPushButton()
        self._toggle_button = QPushButton()
        self._toggle_button.setObjectName("primaryButton")
        self._refresh_button.setIcon(load_icon("ui/action_refresh.svg"))
        self._region_button.setIcon(load_icon("ui/action_region.svg"))
        self._region_label = QLabel()
        self._target_label = QLabel()
        controls.addWidget(self._target_label, 0, 0)
        controls.addWidget(self.window_combo, 0, 1)
        controls.addWidget(self._refresh_button, 0, 2)
        controls.addWidget(self._region_button, 1, 0)
        controls.addWidget(self._region_label, 1, 1)
        controls.addWidget(self._toggle_button, 1, 2)
        card_layout.addLayout(controls)

        self._status_label = QLabel()
        self._status_label.setObjectName("statusPill")
        card_layout.addWidget(self._status_label)
        layout.addWidget(controls_card)
        self._diag_title = QLabel()
        self._diag_title.setObjectName("cardTitle")
        layout.addWidget(self._diag_title)
        self.table = QTableWidget(0, 4)
        self._table_headers = ["_time", "_original", "_translated", "_confidence"]
        self.table.setAlternatingRowColors(True)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.verticalHeader().setVisible(False)
        self.table.setColumnWidth(0, 80)
        self.table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        self.table.horizontalHeader().setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        self.table.setColumnWidth(3, 70)
        layout.addWidget(self.table)

        self._refresh_button.clicked.connect(self.refresh_requested)
        self._region_button.clicked.connect(self.region_selection_requested)
        self._toggle_button.clicked.connect(self.toggle_requested)

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._title.setText(t("page.ocr.title"))
        self._subtitle.setText(t("page.ocr.subtitle"))
        self._privacy.setText(t("page.ocr.privacy"))
        self._target_label.setText(t("page.ocr.target_window"))
        self._refresh_button.setText(t("page.ocr.refresh_button"))
        self._region_button.setText(t("page.ocr.region_button"))
        self._toggle_button.setText(
            t("page.ocr.toggle_stop") if self._running else t("page.ocr.toggle_start")
        )
        self._diag_title.setText(t("page.ocr.diagnostic_title"))
        headers = [
            t("page.ocr.col_time"),
            t("page.ocr.col_original"),
            t("page.ocr.col_translated"),
            t("page.ocr.col_confidence"),
        ]
        self.table.setHorizontalHeaderLabels(headers)

    @property
    def selected_hwnd(self) -> int | None:
        value = self.window_combo.currentData()
        return int(value) if value is not None else None

    def set_windows(self, windows: list[WindowInfo]) -> None:
        selected = self.selected_hwnd
        self.window_combo.clear()
        for window in windows:
            self.window_combo.addItem(window.display_name, window.hwnd)
        if selected is not None:
            index = self.window_combo.findData(selected)
            if index >= 0:
                self.window_combo.setCurrentIndex(index)

    def set_region(self, settings: OcrSettings) -> None:
        if settings.region_width > 0 and settings.region_height > 0:
            self._region_label.setText(
                f"x={settings.region_x}, y={settings.region_y}, "
                f"{settings.region_width}\u00d7{settings.region_height}"
            )
        else:
            self._region_label.setText(self._i18n.tr("page.ocr.region_none"))

    def set_running(self, running: bool) -> None:
        self._running = running
        self._toggle_button.setText(
            self._i18n.tr("page.ocr.toggle_stop") if running else self._i18n.tr("page.ocr.toggle_start")
        )
        self._toggle_button.setEnabled(True)
        self._region_button.setEnabled(not running)
        self._refresh_button.setEnabled(not running)

    def set_stopping(self) -> None:
        self._toggle_button.setEnabled(False)

    def set_status(self, message: str) -> None:
        self._status_label.setText(message)

    def add_recognition(
        self, request_id: str, original: str, confidence: float
    ) -> None:
        self.table.insertRow(0)
        original_item = QTableWidgetItem(original)
        original_item.setData(Qt.ItemDataRole.UserRole, request_id)
        self.table.setItem(0, 0, QTableWidgetItem(datetime.now().strftime("%H:%M:%S")))
        self.table.setItem(0, 1, original_item)
        self.table.setItem(0, 2, QTableWidgetItem(self._i18n.tr("page.ocr.translating")))
        self.table.setItem(0, 3, QTableWidgetItem(f"{confidence:.0%}"))
        while self.table.rowCount() > 100:
            self.table.removeRow(self.table.rowCount() - 1)

    def set_translation(self, request_id: str, text: str) -> None:
        for row in range(self.table.rowCount()):
            original_item = self.table.item(row, 1)
            if original_item and original_item.data(Qt.ItemDataRole.UserRole) == request_id:
                self.table.setItem(row, 2, QTableWidgetItem(text))
                return
