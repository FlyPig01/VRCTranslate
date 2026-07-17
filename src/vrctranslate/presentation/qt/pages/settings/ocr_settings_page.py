from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QImage, QPixmap
from PySide6.QtWidgets import (
    QHBoxLayout,
    QLabel,
    QProgressBar,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.common import card, form_layout, scroll_page
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit


class OcrSettingsPage(QWidget):
    capture_test_requested = Signal(str)
    model_install_requested = Signal(str)
    model_remove_requested = Signal(str)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        scroll, _, layout = scroll_page()
        root.addWidget(scroll)

        models, models_layout = card("")
        self._models_card_title = models_layout.itemAt(0).widget()
        self._models_card_title.setObjectName("cardTitle")
        self._models_note = QLabel()
        self._models_note.setWordWrap(True)
        self._models_note.setObjectName("inlineNotice")
        models_layout.addWidget(self._models_note)
        self._model_names: dict[str, QLabel] = {}
        self._model_statuses: dict[str, QLabel] = {}
        self._model_progress: dict[str, QProgressBar] = {}
        self._model_install_buttons: dict[str, QPushButton] = {}
        self._model_remove_buttons: dict[str, QPushButton] = {}
        self._model_state: dict[str, tuple[bool, str, int]] = {}
        for language in ("zh-CN", "ja"):
            row = QHBoxLayout()
            row.setSpacing(8)
            name = QLabel()
            name.setObjectName("cardTitle")
            status = QLabel()
            status.setWordWrap(True)
            progress = QProgressBar()
            progress.setTextVisible(True)
            progress.setRange(0, 100)
            progress.setValue(0)
            progress.setFixedWidth(120)
            install = QPushButton()
            install.setObjectName("primaryButton")
            remove = QPushButton()
            install.clicked.connect(
                lambda _checked=False, value=language: self.model_install_requested.emit(value)
            )
            remove.clicked.connect(
                lambda _checked=False, value=language: self.model_remove_requested.emit(value)
            )
            row.addWidget(name)
            row.addWidget(status, 1)
            row.addWidget(progress)
            row.addWidget(install)
            row.addWidget(remove)
            models_layout.addLayout(row)
            self._model_names[language] = name
            self._model_statuses[language] = status
            self._model_progress[language] = progress
            self._model_install_buttons[language] = install
            self._model_remove_buttons[language] = remove
            self._model_state[language] = (False, "", 0)
        layout.addWidget(models)

        capture, capture_layout = card("")
        self._capture_card_title = capture_layout.itemAt(0).widget()
        self._capture_card_title.setObjectName("cardTitle")
        form = form_layout()
        self.capture_backend_combo = NoWheelComboBox()
        self.ocr_interval_spin = NumericLineEdit(250, 10000)
        self.ocr_confidence_spin = NumericLineEdit(0.0, 1.0, 2)
        self.ocr_change_spin = NumericLineEdit(0.0, 255.0, 1)
        form.addRow(self._i18n.tr("ocr_settings.backend"), self.capture_backend_combo)
        form.addRow(self._i18n.tr("ocr_settings.interval"), self.ocr_interval_spin)
        form.addRow(self._i18n.tr("ocr_settings.confidence"), self.ocr_confidence_spin)
        form.addRow(self._i18n.tr("ocr_settings.change"), self.ocr_change_spin)
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
            lambda: self.capture_test_requested.emit(str(self.capture_backend_combo.currentData()))
        )
        layout.addWidget(capture)

        scheduler, scheduler_layout = card("")
        self._scheduler_card_title = scheduler_layout.itemAt(0).widget()
        self._scheduler_card_title.setObjectName("cardTitle")
        scheduler_form = form_layout()
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
        layout.addStretch()

        self._retranslate()
        i18n.language_changed.connect(lambda *_: self._retranslate())

    def _retranslate(self) -> None:
        self._models_card_title.setText(self._i18n.tr("ocr_models.card"))
        self._models_note.setText(self._i18n.tr("ocr_models.note"))
        self._model_names["zh-CN"].setText(self._i18n.tr("ocr_models.zh_name"))
        self._model_names["ja"].setText(self._i18n.tr("ocr_models.ja_name"))
        for language in ("zh-CN", "ja"):
            self._model_install_buttons[language].setText(
                self._i18n.tr("ocr_models.install")
            )
            self._model_remove_buttons[language].setText(
                self._i18n.tr("ocr_models.remove")
            )
            installed, version, installed_size = self._model_state[language]
            self.set_model_status(language, installed, version, installed_size)
        self._capture_card_title.setText(self._i18n.tr("ocr_settings.capture_card"))
        self._rebuild_backend_combo()
        self.capture_backend_status.setText(self._i18n.tr("ocr_settings.backend_status"))
        self.capture_test_button.setText(self._i18n.tr("ocr_settings.test_capture"))
        if self.capture_preview.pixmap() is None:
            self.capture_preview.setText(self._i18n.tr("ocr_settings.preview_hint"))
        self._scheduler_card_title.setText(self._i18n.tr("ocr_settings.scheduler_card"))
        self._scheduler_note.setText(self._i18n.tr("ocr_settings.scheduler_note"))

    def _rebuild_backend_combo(self) -> None:
        current = self.capture_backend_combo.currentData()
        self.capture_backend_combo.clear()
        for label, value in (
            (self._i18n.tr("ocr_settings.backend_auto"), "auto"),
            (self._i18n.tr("ocr_settings.backend_windows"), "windows"),
            (self._i18n.tr("ocr_settings.backend_screen"), "screen"),
        ):
            self.capture_backend_combo.addItem(label, value)
        index = self.capture_backend_combo.findData(current)
        if index >= 0:
            self.capture_backend_combo.setCurrentIndex(index)

    def load_settings(self, settings: AppSettings) -> None:
        index = self.capture_backend_combo.findData(settings.ocr.capture_backend)
        if index >= 0:
            self.capture_backend_combo.setCurrentIndex(index)
        self.ocr_interval_spin.setValue(settings.ocr.interval_ms)
        self.ocr_confidence_spin.setValue(settings.ocr.confidence)
        self.ocr_change_spin.setValue(settings.ocr.change_threshold)
        self.ocr_queue_spin.setValue(settings.translation.ocr_route.queue_limit)
        self.ocr_ttl_spin.setValue(settings.translation.ocr_route.task_ttl_seconds)

    def collect_settings(self, settings: AppSettings) -> None:
        settings.ocr.capture_backend = str(self.capture_backend_combo.currentData())
        settings.ocr.interval_ms = int(self.ocr_interval_spin.value())
        settings.ocr.confidence = float(self.ocr_confidence_spin.value())
        settings.ocr.change_threshold = float(self.ocr_change_spin.value())
        settings.translation.ocr_route.queue_limit = int(self.ocr_queue_spin.value())
        settings.translation.ocr_route.task_ttl_seconds = float(self.ocr_ttl_spin.value())

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
                pixels.data, int(width), int(height), stride, QImage.Format.Format_BGR888  # type: ignore[attr-defined]
            ).copy()
            self.capture_preview.setPixmap(
                QPixmap.fromImage(image).scaled(
                    self.capture_preview.size(),
                    Qt.AspectRatioMode.KeepAspectRatio,
                    Qt.TransformationMode.SmoothTransformation,
                )
            )
        except Exception:
            self.capture_preview.setText(self._i18n.tr("ocr_settings.unsupported"))

    def clear_preview(self) -> None:
        self.capture_preview.clear()
        self.capture_preview.setText(self._i18n.tr("ocr_settings.preview_hint"))

    def set_model_status(
        self,
        language: str,
        installed: bool,
        version: str,
        installed_size: int,
        *,
        busy: bool = False,
        error: str = "",
    ) -> None:
        if language not in self._model_statuses:
            return
        self._model_state[language] = (installed, version, installed_size)
        size_mb = installed_size / (1024 * 1024)
        if error:
            message = self._i18n.tr("ocr_models.failed", error=error)
        elif busy:
            message = self._i18n.tr("ocr_models.downloading")
        elif installed:
            message = self._i18n.tr(
                "ocr_models.installed", version=version, size=f"{size_mb:.1f}"
            )
        else:
            message = self._i18n.tr("ocr_models.not_installed")
        self._model_statuses[language].setText(message)
        progress = self._model_progress[language]
        if busy:
            progress.setRange(0, 0)
        else:
            progress.setRange(0, 100)
            progress.setValue(100 if installed else 0)
        self._model_install_buttons[language].setEnabled(not busy and not installed)
        self._model_remove_buttons[language].setEnabled(not busy and installed)
