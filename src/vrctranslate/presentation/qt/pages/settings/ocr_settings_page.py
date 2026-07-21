from __future__ import annotations

from dataclasses import dataclass

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QImage, QPixmap
from PySide6.QtWidgets import (
    QComboBox,
    QFrame,
    QHBoxLayout,
    QLabel,
    QProgressBar,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings
from vrctranslate.domain.languages import OCR_MODEL_PACKAGE_IDS
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.common import card, form_layout, scroll_page
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit


_OCR_MODEL_LANGUAGES = OCR_MODEL_PACKAGE_IDS
_OCR_MODEL_NAME_KEYS = {
    "zh-CN": "ocr_models.zh_name",
    "ja": "ocr_models.ja_name",
    "en": "ocr_models.en_name",
    "ko": "ocr_models.ko_name",
    "latin": "ocr_models.latin_name",
    "cyrillic": "ocr_models.cyrillic_name",
}


@dataclass(slots=True)
class _ModelState:
    installed: bool = False
    version: str = ""
    installed_size: int = 0
    exclusive_size: int = 0
    download_size: int = 0
    busy: bool = False
    error: str = ""
    completed: int = 0
    total: int = 0


class OcrSettingsPage(QWidget):
    capture_test_requested = Signal(str)
    model_install_requested = Signal(str)
    model_remove_requested = Signal(str)
    model_cancel_requested = Signal(str)

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

        toolbar = QFrame()
        toolbar.setObjectName("ocrModelToolbar")
        toolbar_layout = QHBoxLayout(toolbar)
        toolbar_layout.setContentsMargins(12, 10, 12, 10)
        toolbar_layout.setSpacing(10)
        self._model_selector_label = QLabel()
        self._model_selector_label.setObjectName("ocrModelToolbarLabel")
        self.model_selector = NoWheelComboBox()
        self.model_selector.setObjectName("ocrModelSelector")
        self.model_selector.setMinimumContentsLength(12)
        self.model_selector.setSizeAdjustPolicy(
            QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon
        )
        self._model_filter_label = QLabel()
        self._model_filter_label.setObjectName("ocrModelToolbarLabel")
        self.model_filter_combo = NoWheelComboBox()
        self.model_filter_combo.setObjectName("ocrModelFilter")
        self.model_filter_combo.setMinimumContentsLength(8)
        self.model_filter_combo.setSizeAdjustPolicy(
            QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon
        )
        toolbar_layout.addWidget(self._model_selector_label)
        toolbar_layout.addWidget(self.model_selector, 1)
        toolbar_layout.addWidget(self._model_filter_label)
        toolbar_layout.addWidget(self.model_filter_combo)
        models_layout.addWidget(toolbar)

        self._model_names: dict[str, QLabel] = {}
        self._model_statuses: dict[str, QLabel] = {}
        self._model_details: dict[str, QLabel] = {}
        self._model_progress: dict[str, QProgressBar] = {}
        self._model_install_buttons: dict[str, QPushButton] = {}
        self._model_remove_buttons: dict[str, QPushButton] = {}
        self._model_cancel_buttons: dict[str, QPushButton] = {}
        self._model_state = {
            language: _ModelState() for language in _OCR_MODEL_LANGUAGES
        }

        surface = QFrame()
        surface.setObjectName("ocrModelCard")
        surface_layout = QVBoxLayout(surface)
        surface_layout.setContentsMargins(14, 12, 14, 12)
        surface_layout.setSpacing(8)

        header = QHBoxLayout()
        name = QLabel()
        name.setObjectName("ocrModelName")
        name.setWordWrap(True)
        status = QLabel()
        status.setObjectName("ocrModelStatus")
        header.addWidget(name, 1)
        header.addStretch()
        header.addWidget(status)
        surface_layout.addLayout(header)

        detail = QLabel()
        detail.setObjectName("ocrModelDetail")
        detail.setWordWrap(True)
        surface_layout.addWidget(detail)

        progress = QProgressBar()
        progress.setTextVisible(True)
        progress.hide()
        surface_layout.addWidget(progress)

        actions = QHBoxLayout()
        actions.addStretch()
        install = QPushButton()
        install.setObjectName("primaryButton")
        remove = QPushButton()
        cancel = QPushButton()
        actions.addWidget(install)
        actions.addWidget(remove)
        actions.addWidget(cancel)
        surface_layout.addLayout(actions)
        models_layout.addWidget(surface)
        self._model_name_label = name
        self._model_status_label = status
        self._model_detail_label = detail
        self._model_progress_bar = progress
        self._model_install_button = install
        self._model_remove_button = remove
        self._model_cancel_button = cancel

        install.clicked.connect(self._request_selected_install)
        remove.clicked.connect(self._request_selected_remove)
        cancel.clicked.connect(self._request_selected_cancel)
        self.model_selector.currentIndexChanged.connect(
            lambda _index: self._render_selected_model()
        )
        self.model_filter_combo.currentIndexChanged.connect(
            lambda _index: self._rebuild_model_selector()
        )

        # Keep the per-language lookup attributes used by controllers and
        # presentation tests.  The optimized page renders one selected model,
        # so every entry intentionally points at the shared detail widgets.
        for language in _OCR_MODEL_LANGUAGES:
            self._model_names[language] = name
            self._model_statuses[language] = status
            self._model_details[language] = detail
            self._model_progress[language] = progress
            self._model_install_buttons[language] = install
            self._model_remove_buttons[language] = remove
            self._model_cancel_buttons[language] = cancel

        summary_row = QHBoxLayout()
        self._model_install_summary = QLabel()
        self._model_install_summary.setObjectName("ocrModelCount")
        self._model_storage_summary = QLabel()
        self._model_storage_summary.setObjectName("ocrModelStorage")
        self._model_storage_summary.setWordWrap(True)
        summary_row.addWidget(self._model_install_summary)
        summary_row.addWidget(self._model_storage_summary, 1)
        models_layout.addLayout(summary_row)
        self._shared_model_size = 0
        self._total_model_size = 0
        layout.addWidget(models)

        capture, capture_layout = card("")
        self._capture_card_title = capture_layout.itemAt(0).widget()
        self._capture_card_title.setObjectName("cardTitle")
        form = form_layout()
        self.capture_backend_combo = NoWheelComboBox()
        self.ocr_package_combo = NoWheelComboBox()
        self._ocr_package_label = QLabel()
        self.ocr_interval_spin = NumericLineEdit(250, 10000)
        self.ocr_confidence_spin = NumericLineEdit(0.0, 1.0, 2)
        self.ocr_change_spin = NumericLineEdit(0.0, 255.0, 1)
        self.multimodal_interval_spin = NumericLineEdit(1000, 60000)
        form.addRow(self._i18n.tr("ocr_settings.backend"), self.capture_backend_combo)
        form.addRow(self._ocr_package_label, self.ocr_package_combo)
        form.addRow(self._i18n.tr("ocr_settings.interval"), self.ocr_interval_spin)
        form.addRow(self._i18n.tr("ocr_settings.confidence"), self.ocr_confidence_spin)
        form.addRow(self._i18n.tr("ocr_settings.change"), self.ocr_change_spin)
        form.addRow(
            self._i18n.tr("ocr_settings.multimodal_interval"),
            self.multimodal_interval_spin,
        )
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

    @staticmethod
    def _mib(size: int) -> str:
        return f"{size / (1024 * 1024):.1f}"

    def _retranslate(self) -> None:
        self._models_card_title.setText(self._i18n.tr("ocr_models.card"))
        self._models_note.setText(self._i18n.tr("ocr_models.note_compact"))
        self._models_note.setToolTip(self._i18n.tr("ocr_models.note"))
        self._model_selector_label.setText(
            self._i18n.tr("ocr_models.selector_label")
        )
        self._model_filter_label.setText(
            self._i18n.tr("ocr_models.filter_label")
        )
        current_filter = str(self.model_filter_combo.currentData() or "all")
        self.model_filter_combo.blockSignals(True)
        self.model_filter_combo.clear()
        for key, value in (
            ("ocr_models.filter_all", "all"),
            ("ocr_models.filter_installed", "installed"),
            ("ocr_models.filter_missing", "missing"),
        ):
            self.model_filter_combo.addItem(self._i18n.tr(key), value)
        filter_index = self.model_filter_combo.findData(current_filter)
        self.model_filter_combo.setCurrentIndex(filter_index if filter_index >= 0 else 0)
        self.model_filter_combo.blockSignals(False)
        self._rebuild_model_selector()
        self._render_storage()
        self._capture_card_title.setText(self._i18n.tr("ocr_settings.capture_card"))
        self._rebuild_backend_combo()
        self._ocr_package_label.setText(
            self._i18n.tr("ocr_settings.model_package")
        )
        self._rebuild_ocr_package_combo()
        self.capture_backend_status.setText(self._i18n.tr("ocr_settings.backend_status"))
        self.capture_test_button.setText(self._i18n.tr("ocr_settings.test_capture"))
        if self.capture_preview.pixmap() is None:
            self.capture_preview.setText(self._i18n.tr("ocr_settings.preview_hint"))
        self._scheduler_card_title.setText(self._i18n.tr("ocr_settings.scheduler_card"))
        self._scheduler_note.setText(self._i18n.tr("ocr_settings.scheduler_note"))

    def _render_selected_model(self) -> None:
        language = self._selected_model()
        if language is None:
            self._model_name_label.setText(
                self._i18n.tr("ocr_models.filter_empty")
            )
            self._model_status_label.hide()
            self._model_detail_label.clear()
            self._model_progress_bar.hide()
            self._model_install_button.hide()
            self._model_remove_button.hide()
            self._model_cancel_button.hide()
            return
        state = self._model_state[language]
        name = self._model_names[language]
        status = self._model_statuses[language]
        detail = self._model_details[language]
        progress = self._model_progress[language]
        install = self._model_install_buttons[language]
        remove = self._model_remove_buttons[language]
        cancel = self._model_cancel_buttons[language]
        status.show()
        name.setText(self._model_name(language))
        remove.setText(self._i18n.tr("ocr_models.remove"))
        cancel.setText(self._i18n.tr("ocr_models.cancel"))

        if state.error:
            view_state = "error"
            status.setText(self._i18n.tr("ocr_models.failed_badge"))
            detail.setText(self._i18n.tr("ocr_models.failed", error=state.error))
        elif state.busy:
            view_state = "busy"
            if state.total > 0 and state.completed >= state.total:
                status.setText(self._i18n.tr("ocr_models.verifying_badge"))
                detail.setText(self._i18n.tr("ocr_models.verifying"))
            else:
                status.setText(self._i18n.tr("ocr_models.downloading_badge"))
                detail.setText(
                    self._i18n.tr(
                        "ocr_models.downloading_progress",
                        completed=self._mib(state.completed),
                        total=self._mib(state.total or state.download_size),
                    )
                )
        elif state.installed:
            view_state = "installed"
            status.setText(self._i18n.tr("ocr_models.installed_badge"))
            detail.setText(
                self._i18n.tr(
                    "ocr_models.installed",
                    version=state.version,
                    size=self._mib(state.exclusive_size),
                )
            )
        else:
            view_state = "missing"
            status.setText(self._i18n.tr("ocr_models.not_installed"))
            detail.setText(
                self._i18n.tr(
                    "ocr_models.available",
                    version=state.version or "-",
                    size=self._mib(state.download_size),
                )
            )

        status.setProperty("state", view_state)
        status.style().unpolish(status)
        status.style().polish(status)

        progress.setVisible(state.busy)
        if state.busy:
            total = state.total or state.download_size
            if total > 0:
                progress.setRange(0, total)
                progress.setValue(min(state.completed, total))
                progress.setFormat("%p%")
            else:
                progress.setRange(0, 0)

        install.setVisible(not state.busy and not state.installed)
        install.setText(
            self._i18n.tr("ocr_models.retry" if state.error else "ocr_models.install")
        )
        remove.setVisible(not state.busy and state.installed)
        cancel.setVisible(state.busy)

    def _model_name(self, language: str) -> str:
        return self._i18n.tr(_OCR_MODEL_NAME_KEYS[language])

    def _selected_model(self) -> str | None:
        language = str(self.model_selector.currentData() or "")
        return language if language in self._model_state else None

    def _selector_status(self, state: _ModelState) -> str:
        if state.error:
            return self._i18n.tr("ocr_models.failed_badge")
        if state.busy:
            return self._i18n.tr("ocr_models.downloading_badge")
        if state.installed:
            return self._i18n.tr("ocr_models.installed_badge")
        return self._i18n.tr("ocr_models.not_installed")

    def _matches_model_filter(self, state: _ModelState, mode: str) -> bool:
        if mode == "installed":
            return state.installed
        if mode == "missing":
            return not state.installed
        return True

    def _rebuild_model_selector(self, preferred: str | None = None) -> None:
        current = preferred or self._selected_model()
        mode = str(self.model_filter_combo.currentData() or "all")
        self.model_selector.blockSignals(True)
        self.model_selector.clear()
        for language in _OCR_MODEL_LANGUAGES:
            state = self._model_state[language]
            if not self._matches_model_filter(state, mode):
                continue
            label = f"{self._model_name(language)}  ·  {self._selector_status(state)}"
            self.model_selector.addItem(label, language)
        index = self.model_selector.findData(current)
        self.model_selector.setCurrentIndex(index if index >= 0 else 0)
        self.model_selector.blockSignals(False)
        self._render_selected_model()

    def _request_selected_install(self) -> None:
        language = self._selected_model()
        if language is not None:
            self.model_install_requested.emit(language)

    def _request_selected_remove(self) -> None:
        language = self._selected_model()
        if language is not None:
            self.model_remove_requested.emit(language)

    def _request_selected_cancel(self) -> None:
        language = self._selected_model()
        if language is not None:
            self.model_cancel_requested.emit(language)

    def _render_storage(self) -> None:
        installed = sum(state.installed for state in self._model_state.values())
        self._model_install_summary.setText(
            self._i18n.tr(
                "ocr_models.installed_count",
                installed=installed,
                total=len(self._model_state),
            )
        )
        self._model_storage_summary.setText(
            self._i18n.tr(
                "ocr_models.storage_summary",
                shared=self._mib(self._shared_model_size),
                total=self._mib(self._total_model_size),
            )
        )

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

    def _rebuild_ocr_package_combo(self) -> None:
        current = str(self.ocr_package_combo.currentData() or "ja")
        self.ocr_package_combo.blockSignals(True)
        self.ocr_package_combo.clear()
        for package_id in _OCR_MODEL_LANGUAGES:
            self.ocr_package_combo.addItem(
                self._model_name(package_id),
                package_id,
            )
        index = self.ocr_package_combo.findData(current)
        self.ocr_package_combo.setCurrentIndex(index if index >= 0 else 0)
        self.ocr_package_combo.blockSignals(False)

    def load_settings(self, settings: AppSettings) -> None:
        index = self.capture_backend_combo.findData(settings.ocr.capture_backend)
        if index >= 0:
            self.capture_backend_combo.setCurrentIndex(index)
        package_index = self.ocr_package_combo.findData(settings.ocr.model_package)
        if package_index >= 0:
            self.ocr_package_combo.setCurrentIndex(package_index)
        self.ocr_interval_spin.setValue(settings.ocr.interval_ms)
        self.ocr_confidence_spin.setValue(settings.ocr.confidence)
        self.ocr_change_spin.setValue(settings.ocr.change_threshold)
        self.multimodal_interval_spin.setValue(settings.ocr.multimodal_interval_ms)
        self.ocr_queue_spin.setValue(settings.translation.ocr_route.queue_limit)
        self.ocr_ttl_spin.setValue(settings.translation.ocr_route.task_ttl_seconds)

    def collect_settings(self, settings: AppSettings) -> None:
        settings.ocr.capture_backend = str(self.capture_backend_combo.currentData())
        settings.ocr.model_package = str(
            self.ocr_package_combo.currentData() or "ja"
        )
        settings.ocr.interval_ms = int(self.ocr_interval_spin.value())
        settings.ocr.confidence = float(self.ocr_confidence_spin.value())
        settings.ocr.change_threshold = float(self.ocr_change_spin.value())
        settings.ocr.multimodal_interval_ms = int(
            self.multimodal_interval_spin.value()
        )
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
                pixels.data,
                int(width),
                int(height),
                stride,
                QImage.Format.Format_BGR888,  # type: ignore[attr-defined]
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
        download_size: int = 0,
        exclusive_size: int = 0,
        busy: bool = False,
        error: str = "",
    ) -> None:
        if language not in self._model_state:
            return
        self._model_state[language] = _ModelState(
            installed=installed,
            version=version,
            installed_size=installed_size,
            exclusive_size=exclusive_size,
            download_size=download_size,
            busy=busy,
            error=error,
            completed=0,
            total=download_size,
        )
        preferred = language if busy or error else self._selected_model()
        self._rebuild_model_selector(preferred=preferred)
        self._render_storage()

    def set_model_progress(self, language: str, completed: int, total: int) -> None:
        state = self._model_state.get(language)
        if state is None:
            return
        state.busy = True
        state.error = ""
        state.completed = max(0, completed)
        state.total = max(0, total)
        self._rebuild_model_selector(preferred=self._selected_model())

    def set_model_storage(self, shared_size: int, total_size: int) -> None:
        self._shared_model_size = max(0, shared_size)
        self._total_model_size = max(0, total_size)
        self._render_storage()
