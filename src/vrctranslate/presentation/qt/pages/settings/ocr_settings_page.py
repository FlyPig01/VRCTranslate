from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QImage, QPixmap
from PySide6.QtWidgets import (
    QCheckBox,
    QFormLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.common import card, scroll_page
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit


class OcrSettingsPage(QWidget):
    capture_test_requested = Signal(str)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        scroll, _, layout = scroll_page()
        root.addWidget(scroll)

        capture, capture_layout = card("")
        self._capture_card_title = capture_layout.itemAt(0).widget()
        self._capture_card_title.setObjectName("cardTitle")
        form = QFormLayout()
        self.capture_backend_combo = NoWheelComboBox()
        self.ocr_interval_spin = NumericLineEdit(250, 10000)
        self.ocr_confidence_spin = NumericLineEdit(0.0, 1.0, 2)
        self.ocr_change_spin = NumericLineEdit(0.0, 255.0, 1)
        self.ocr_duplicate_spin = NumericLineEdit(0.0, 300.0, 1)
        form.addRow(self._i18n.tr("ocr_settings.backend"), self.capture_backend_combo)
        form.addRow(self._i18n.tr("ocr_settings.interval"), self.ocr_interval_spin)
        form.addRow(self._i18n.tr("ocr_settings.confidence"), self.ocr_confidence_spin)
        form.addRow(self._i18n.tr("ocr_settings.change"), self.ocr_change_spin)
        form.addRow(self._i18n.tr("ocr_settings.duplicate"), self.ocr_duplicate_spin)
        capture_layout.addLayout(form)
        self.capture_backend_status = QLabel()
        self.capture_backend_status.setWordWrap(True)
        self.capture_backend_status.setObjectName("inlineNotice")
        capture_layout.addWidget(self.capture_backend_status)
        preview_row = QHBoxLayout()
        self.capture_test_button = QPushButton()
        self.capture_preview = QLabel()
        self.capture_preview.setObjectName("capturePreview")
        self.capture_preview.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.capture_preview.setMinimumSize(220, 110)
        self.capture_preview.setMaximumHeight(180)
        preview_row.addWidget(self.capture_test_button)
        preview_row.addWidget(self.capture_preview, 1)
        capture_layout.addLayout(preview_row)
        self.capture_test_button.clicked.connect(
            lambda: self.capture_test_requested.emit(
                str(self.capture_backend_combo.currentData())
            )
        )
        layout.addWidget(capture)

        scheduler, scheduler_layout = card("")
        self._scheduler_card_title = scheduler_layout.itemAt(0).widget()
        self._scheduler_card_title.setObjectName("cardTitle")
        scheduler_form = QFormLayout()
        self.ocr_queue_spin = NumericLineEdit(1, 100)
        self.ocr_ttl_spin = NumericLineEdit(0.5, 60.0, 1)
        scheduler_form.addRow(self._i18n.tr("ocr_settings.queue_limit"), self.ocr_queue_spin)
        scheduler_form.addRow(self._i18n.tr("ocr_settings.ttl"), self.ocr_ttl_spin)
        scheduler_layout.addLayout(scheduler_form)
        self._scheduler_note = QLabel()
        self._scheduler_note.setWordWrap(True)
        self._scheduler_note.setObjectName("inlineNotice")
        scheduler_layout.addWidget(self._scheduler_note)
        layout.addWidget(scheduler)

        overlay, overlay_layout = card("")
        self._overlay_card_title = overlay_layout.itemAt(0).widget()
        self._overlay_card_title.setObjectName("cardTitle")
        overlay_form = QFormLayout()
        self.ocr_topmost_check = QCheckBox()
        self.ocr_passthrough_check = QCheckBox()
        self._ocr_passthrough_hint = QLabel()
        self._ocr_passthrough_hint.setWordWrap(True)
        self._ocr_passthrough_hint.setObjectName("inlineNotice")
        self.overlay_opacity_spin = NumericLineEdit(0.25, 1.0, 2)
        self.overlay_font_spin = NumericLineEdit(10, 40)
        self.overlay_items_spin = NumericLineEdit(1, 20)
        self.overlay_duration_spin = NumericLineEdit(2.0, 120.0, 1)
        overlay_form.addRow(self.ocr_topmost_check)
        overlay_form.addRow(self.ocr_passthrough_check)
        overlay_form.addRow("", self._ocr_passthrough_hint)
        overlay_form.addRow(self._i18n.tr("ocr_settings.opacity"), self.overlay_opacity_spin)
        overlay_form.addRow(self._i18n.tr("ocr_settings.font_size"), self.overlay_font_spin)
        overlay_form.addRow(self._i18n.tr("ocr_settings.max_items"), self.overlay_items_spin)
        overlay_form.addRow(self._i18n.tr("ocr_settings.duration"), self.overlay_duration_spin)
        overlay_layout.addLayout(overlay_form)
        layout.addWidget(overlay)
        layout.addStretch()

        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

    def _retranslate(self) -> None:
        self._capture_card_title.setText(self._i18n.tr("ocr_settings.capture_card"))
        self._rebuild_backend_combo()
        self.capture_backend_status.setText(self._i18n.tr("ocr_settings.backend_status"))
        self.capture_test_button.setText(self._i18n.tr("ocr_settings.test_capture"))
        self.capture_preview.setText(self._i18n.tr("ocr_settings.preview_hint"))
        self._scheduler_card_title.setText(self._i18n.tr("ocr_settings.scheduler_card"))
        self._scheduler_note.setText(self._i18n.tr("ocr_settings.scheduler_note"))
        self._overlay_card_title.setText(self._i18n.tr("ocr_settings.overlay_card"))
        self.ocr_topmost_check.setText(self._i18n.tr("ocr_settings.topmost"))
        self.ocr_passthrough_check.setText(self._i18n.tr("ocr_settings.passthrough"))
        self._ocr_passthrough_hint.setText(self._i18n.tr("ocr_settings.passthrough_hint"))

    def _rebuild_backend_combo(self) -> None:
        current = self.capture_backend_combo.currentData()
        self.capture_backend_combo.clear()
        items = (
            (self._i18n.tr("ocr_settings.backend_auto"), "auto"),
            (self._i18n.tr("ocr_settings.backend_windows"), "windows"),
            (self._i18n.tr("ocr_settings.backend_screen"), "screen"),
        )
        for label, value in items:
            self.capture_backend_combo.addItem(label, value)
        idx = self.capture_backend_combo.findData(current)
        if idx >= 0:
            self.capture_backend_combo.setCurrentIndex(idx)

    def load_settings(self, settings: AppSettings) -> None:
        backend = getattr(settings.ocr, "capture_backend", "auto")
        index = self.capture_backend_combo.findData(backend)
        if index >= 0:
            self.capture_backend_combo.setCurrentIndex(index)
        self.ocr_interval_spin.setValue(settings.ocr.interval_ms)
        self.ocr_confidence_spin.setValue(settings.ocr.confidence)
        self.ocr_change_spin.setValue(settings.ocr.change_threshold)
        self.ocr_duplicate_spin.setValue(settings.ocr.duplicate_seconds)
        self.ocr_queue_spin.setValue(settings.translation.ocr_route.queue_limit)
        self.ocr_ttl_spin.setValue(settings.translation.ocr_route.task_ttl_seconds)
        self.ocr_topmost_check.setChecked(settings.ui.ocr_topmost)
        self.ocr_passthrough_check.setChecked(settings.ui.ocr_mouse_passthrough)
        self.overlay_opacity_spin.setValue(settings.ui.ocr_overlay_opacity)
        self.overlay_font_spin.setValue(settings.ui.ocr_overlay_font_size)
        self.overlay_items_spin.setValue(settings.ui.ocr_overlay_max_items)
        self.overlay_duration_spin.setValue(settings.ui.ocr_overlay_display_seconds)

    def collect_settings(self, settings: AppSettings) -> None:
        settings.ocr.capture_backend = str(self.capture_backend_combo.currentData())
        settings.ocr.interval_ms = int(self.ocr_interval_spin.value())
        settings.ocr.confidence = float(self.ocr_confidence_spin.value())
        settings.ocr.change_threshold = float(self.ocr_change_spin.value())
        settings.ocr.duplicate_seconds = float(self.ocr_duplicate_spin.value())
        settings.translation.ocr_route.queue_limit = int(self.ocr_queue_spin.value())
        settings.translation.ocr_route.task_ttl_seconds = float(self.ocr_ttl_spin.value())
        settings.ui.ocr_topmost = self.ocr_topmost_check.isChecked()
        settings.ui.ocr_mouse_passthrough = self.ocr_passthrough_check.isChecked()
        settings.ui.ocr_overlay_opacity = float(self.overlay_opacity_spin.value())
        settings.ui.ocr_overlay_font_size = int(self.overlay_font_spin.value())
        settings.ui.ocr_overlay_max_items = int(self.overlay_items_spin.value())
        settings.ui.ocr_overlay_display_seconds = float(self.overlay_duration_spin.value())

    def set_capture_status(self, message: str) -> None:
        self.capture_backend_status.setText(message)

    def set_capture_preview(self, pixels: object | None, message: str) -> None:
        self.set_capture_status(message)
        if pixels is None:
            self.capture_preview.clear()
            self.capture_preview.setText(self._i18n.tr("ocr_settings.no_frame"))
            return
        try:
            height, width = pixels.shape[:2]  # type: ignore[attr-defined]
            stride = int(pixels.strides[0])  # type: ignore[attr-defined]
            image = QImage(
                pixels.data,  # type: ignore[attr-defined]
                int(width),
                int(height),
                stride,
                QImage.Format.Format_BGR888,
            ).copy()
            pixmap = QPixmap.fromImage(image).scaled(
                self.capture_preview.size(),
                Qt.AspectRatioMode.KeepAspectRatio,
                Qt.TransformationMode.SmoothTransformation,
            )
            self.capture_preview.setPixmap(pixmap)
        except Exception:
            self.capture_preview.setText(self._i18n.tr("ocr_settings.unsupported"))

    def clear_preview(self) -> None:
        self.capture_preview.clear()
        self.capture_preview.setText(self._i18n.tr("ocr_settings.preview_hint"))
