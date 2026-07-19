from __future__ import annotations

from PySide6.QtCore import QTimer, Qt, Signal
from PySide6.QtWidgets import (
    QBoxLayout,
    QCheckBox,
    QComboBox,
    QFormLayout,
    QFrame,
    QGraphicsOpacityEffect,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings
from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.font_utils import font_with_pixel_height
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.options import languages
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit


class VoicePage(QWidget):
    refresh_targets_requested = Signal()
    target_selected = Signal(int)
    overlay_show_requested = Signal()
    overlay_clear_requested = Signal()
    overlay_reset_requested = Signal()
    overlay_settings_changed = Signal(bool, str, float, int, int)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._loading_targets = False
        self._loading_settings = False
        self._last_original = ""
        self._last_translated = ""
        self._partial = False
        self._narrow_layout: bool | None = None
        self._layout_refresh_timer = QTimer(self)
        self._layout_refresh_timer.setSingleShot(True)
        self._layout_refresh_timer.setInterval(0)
        self._layout_refresh_timer.timeout.connect(
            self._apply_responsive_layout
        )
        self._runtime = {
            "status": "idle",
            "source": "auto",
            "target": "zh-CN",
            "service": "-",
            "translator": "-",
            "strategy": "text_profile",
        }
        self._build_ui()
        self._retranslate()
        i18n.language_changed.connect(lambda _: self._retranslate())

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
        self._scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self._content = QWidget()
        self._content.setMinimumWidth(0)
        self._grid = QGridLayout(self._content)
        self._grid.setContentsMargins(0, 0, 0, 0)
        self._grid.setHorizontalSpacing(12)
        self._grid.setVerticalSpacing(12)

        self._summary_card, summary_layout = self._card()
        self._summary_title = self._card_title(summary_layout)
        self._status_label = QLabel()
        self._status_label.setObjectName("statusPill")
        self._language_label = QLabel()
        self._service_label = QLabel()
        self._translator_label = QLabel()
        for label in (
            self._status_label,
            self._language_label,
            self._service_label,
            self._translator_label,
        ):
            label.setWordWrap(True)
            summary_layout.addWidget(label)
        summary_layout.addStretch()

        self._source_card, source_layout = self._card()
        self._source_title = self._card_title(source_layout)
        self._target_label = QLabel()
        source_layout.addWidget(self._target_label)
        target_row = QHBoxLayout()
        target_row.setSpacing(8)
        self.target_combo = NoWheelComboBox()
        self.target_combo.setMinimumWidth(0)
        self.target_combo.setSizePolicy(
            QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed
        )
        self.target_combo.setSizeAdjustPolicy(
            QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon
        )
        self.refresh_button = QPushButton()
        self.refresh_button.setIcon(load_icon("ui/action_refresh.svg"))
        target_row.addWidget(self.target_combo, 1)
        target_row.addWidget(self.refresh_button)
        source_layout.addLayout(target_row)
        source_layout.addStretch()

        self._recent_card, recent_layout = self._card()
        self._recent_title = self._card_title(recent_layout)
        surface = QFrame()
        surface.setObjectName("previewCard")
        surface_layout = QVBoxLayout(surface)
        surface_layout.setContentsMargins(14, 11, 14, 12)
        surface_layout.setSpacing(6)
        self._recent_original = QLabel()
        self._recent_original.setObjectName("previewOriginal")
        self._recent_original.setWordWrap(True)
        self._recent_translation = QLabel()
        self._recent_translation.setObjectName("previewTranslated")
        self._recent_translation.setWordWrap(True)
        surface_layout.addWidget(self._recent_original)
        surface_layout.addWidget(self._recent_translation)
        recent_layout.addWidget(surface)
        self.clear_overlay_button = QPushButton()

        self._overlay_card, overlay_layout = self._card()
        self._overlay_title = self._card_title(overlay_layout)
        display_form = QFormLayout()
        display_form.setFieldGrowthPolicy(
            QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow
        )
        self.display_mode_combo = NoWheelComboBox()
        self._display_mode_label = QLabel()
        display_form.addRow(self._display_mode_label, self.display_mode_combo)
        overlay_layout.addLayout(display_form)
        choices = QWidget()
        choices.setObjectName("settingsChoicePanel")
        self._choice_layout = QBoxLayout(
            QBoxLayout.Direction.LeftToRight,
            choices,
        )
        self._choice_layout.setContentsMargins(12, 8, 12, 8)
        self._choice_layout.setSpacing(10)
        self.overlay_topmost_check = QCheckBox()
        self._choice_layout.addWidget(self.overlay_topmost_check)
        self._choice_layout.addStretch()
        overlay_layout.addWidget(choices)

        overlay_form = QFormLayout()
        overlay_form.setVerticalSpacing(10)
        self.overlay_opacity_edit = NumericLineEdit(25, 100)
        self.overlay_font_edit = NumericLineEdit(10, 40)
        self.overlay_items_edit = NumericLineEdit(1, 10)
        self._opacity_label = QLabel()
        self._font_label = QLabel()
        self._items_label = QLabel()
        overlay_form.addRow(self._opacity_label, self.overlay_opacity_edit)
        overlay_form.addRow(self._font_label, self.overlay_font_edit)
        overlay_form.addRow(self._items_label, self.overlay_items_edit)
        self._form_and_preview = QBoxLayout(
            QBoxLayout.Direction.LeftToRight
        )
        self._form_and_preview.setSpacing(14)
        self._form_and_preview.addLayout(overlay_form, 1)
        self.overlay_style_preview = QFrame()
        self.overlay_style_preview.setObjectName("voiceOverlaySurface")
        self.overlay_style_preview.setMinimumSize(250, 118)
        self._preview_opacity_effect = QGraphicsOpacityEffect(
            self.overlay_style_preview
        )
        self.overlay_style_preview.setGraphicsEffect(
            self._preview_opacity_effect
        )
        preview_layout = QVBoxLayout(self.overlay_style_preview)
        preview_layout.setContentsMargins(14, 11, 14, 12)
        self._preview_item = QFrame()
        self._preview_item.setObjectName("voiceCaptionItem")
        preview_item_layout = QVBoxLayout(self._preview_item)
        preview_item_layout.setContentsMargins(10, 7, 10, 8)
        preview_item_layout.setSpacing(2)
        self._preview_original = QLabel("Hello, world!")
        self._preview_original.setObjectName("voiceCaptionOriginal")
        self._preview_translation = QLabel("你好，世界！")
        self._preview_translation.setObjectName("voiceCaptionTranslation")
        preview_item_layout.addWidget(self._preview_original)
        preview_item_layout.addWidget(self._preview_translation)
        preview_layout.addWidget(self._preview_item)
        preview_layout.addStretch()
        self._form_and_preview.addWidget(self.overlay_style_preview, 1)
        overlay_layout.addLayout(self._form_and_preview)
        overlay_actions = QHBoxLayout()
        self.show_overlay_button = QPushButton()
        self.reset_overlay_button = QPushButton()
        overlay_actions.addWidget(self.show_overlay_button)
        overlay_actions.addWidget(self.reset_overlay_button)
        overlay_actions.addWidget(self.clear_overlay_button)
        overlay_actions.addStretch()
        overlay_layout.addLayout(overlay_actions)

        self._grid.setColumnStretch(0, 1)
        self._grid.setColumnStretch(1, 1)
        self._grid.setRowStretch(3, 1)

        self._scroll.setWidget(self._content)
        root.addWidget(self._scroll, 1)
        self._apply_responsive_layout()

        self.refresh_button.clicked.connect(self.refresh_targets_requested)
        self.target_combo.currentIndexChanged.connect(self._target_changed)
        self.show_overlay_button.clicked.connect(self.overlay_show_requested)
        self.clear_overlay_button.clicked.connect(self.overlay_clear_requested)
        self.reset_overlay_button.clicked.connect(self.overlay_reset_requested)
        self.overlay_topmost_check.checkStateChanged.connect(self._overlay_edited)
        self.display_mode_combo.currentIndexChanged.connect(self._overlay_edited)
        self.overlay_opacity_edit.textChanged.connect(self._overlay_edited)
        self.overlay_font_edit.textChanged.connect(self._overlay_edited)
        self.overlay_items_edit.textChanged.connect(self._overlay_edited)

    @staticmethod
    def _card() -> tuple[QFrame, QVBoxLayout]:
        frame = QFrame()
        frame.setObjectName("card")
        layout = QVBoxLayout(frame)
        layout.setContentsMargins(18, 15, 18, 15)
        layout.setSpacing(8)
        return frame, layout

    @staticmethod
    def _card_title(layout: QVBoxLayout) -> QLabel:
        title = QLabel()
        title.setObjectName("cardTitle")
        layout.addWidget(title)
        return title

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._title.setText(t("page.voice.title"))
        self._subtitle.setText(t("page.voice.subtitle"))
        self._source_title.setText(t("page.voice.source_title"))
        self._target_label.setText(t("page.voice.target_process"))
        self.refresh_button.setText(t("page.voice.refresh"))
        self._summary_title.setText(t("page.voice.summary_title"))
        self._recent_title.setText(t("page.voice.recent_title"))
        self._overlay_title.setText(t("voice_settings.overlay_title"))
        self.overlay_topmost_check.setText(t("voice_settings.topmost"))
        self._display_mode_label.setText(t("voice_settings.display_mode"))
        self._rebuild_display_modes()
        self._opacity_label.setText(t("voice_settings.opacity"))
        self._font_label.setText(t("voice_settings.font_size"))
        self._items_label.setText(t("voice_settings.max_items"))
        self.show_overlay_button.setText(t("page.voice.show_overlay"))
        self.reset_overlay_button.setText(t("page.voice.reset_overlay"))
        self.clear_overlay_button.setText(t("page.voice.clear_overlay"))
        self._refresh_overlay_preview()
        self._refresh_summary()
        self._refresh_recent()

    def _rebuild_display_modes(self) -> None:
        current = self.display_mode_combo.currentData()
        self.display_mode_combo.blockSignals(True)
        self.display_mode_combo.clear()
        for key, value in (
            ("voice_settings.display_translation", "translation"),
            ("voice_settings.display_original", "original"),
            ("voice_settings.display_both", "both"),
        ):
            self.display_mode_combo.addItem(self._i18n.tr(key), value)
        index = self.display_mode_combo.findData(current)
        self.display_mode_combo.setCurrentIndex(index if index >= 0 else 2)
        self.display_mode_combo.blockSignals(False)

    @property
    def has_unsaved_changes(self) -> bool:
        return False

    @property
    def selected_process_id(self) -> int | None:
        value = self.target_combo.currentData()
        return int(value) if value is not None else None

    def selected_window(self) -> WindowInfo | None:
        value = self.target_combo.currentData(Qt.ItemDataRole.UserRole + 1)
        return value if isinstance(value, WindowInfo) else None

    def set_target_windows(
        self, windows: list[WindowInfo], selected_process_id: int | None = None
    ) -> None:
        unique: dict[int, WindowInfo] = {}
        for window in windows:
            if window.process_id > 0 and window.process_id not in unique:
                unique[window.process_id] = window
        self._loading_targets = True
        try:
            self.target_combo.clear()
            if not unique:
                self.target_combo.addItem(self._i18n.tr("page.voice.no_targets"), None)
            else:
                self.target_combo.addItem(self._i18n.tr("page.voice.select_target"), None)
                for process_id, window in unique.items():
                    self.target_combo.addItem(window.display_name, process_id)
                    index = self.target_combo.count() - 1
                    self.target_combo.setItemData(
                        index, window, Qt.ItemDataRole.UserRole + 1
                    )
                index = self.target_combo.findData(selected_process_id)
                self.target_combo.setCurrentIndex(index if index >= 0 else 0)
        finally:
            self._loading_targets = False

    def set_running(self, running: bool, *, starting: bool = False) -> None:
        self.target_combo.setEnabled(not running and not starting)
        self.refresh_button.setEnabled(not running and not starting)

    def set_status(self, status: str) -> None:
        self._runtime["status"] = status
        self._refresh_summary()

    def load_settings(self, settings: AppSettings) -> None:
        route = settings.translation.voice_route
        try:
            service_name = settings.voice.asr_profile().name
        except ValueError:
            service_name = "-"
        try:
            translator = settings.translation.profile(route.profile_id).name
        except KeyError:
            translator = "-"
        self._runtime.update(
            source=route.source_language,
            target=route.target_language,
            service=service_name,
            translator=translator,
            strategy="text_profile",
        )
        overlay = settings.voice.overlay
        self._loading_settings = True
        try:
            self.overlay_topmost_check.setChecked(overlay.topmost)
            mode = overlay.display_mode
            if mode not in {"translation", "original", "both"}:
                mode = "both" if overlay.show_original else "translation"
            mode_index = self.display_mode_combo.findData(mode)
            self.display_mode_combo.setCurrentIndex(
                mode_index if mode_index >= 0 else 2
            )
            self.overlay_opacity_edit.setValue(round(overlay.opacity * 100))
            self.overlay_font_edit.setValue(overlay.font_size)
            self.overlay_items_edit.setValue(overlay.max_items)
        finally:
            self._loading_settings = False
        self._refresh_overlay_preview()
        self._refresh_summary()

    def set_partial_caption(self, original: str, translated: str = "") -> None:
        self._last_original = original
        self._last_translated = translated
        self._partial = True
        self._refresh_recent()

    def set_last_caption(self, original: str, translated: str) -> None:
        self._last_original = original
        self._last_translated = translated
        self._partial = False
        self._refresh_recent()

    def _refresh_summary(self) -> None:
        t = self._i18n.tr
        status = self._runtime["status"]
        status_text = (
            t(f"page.voice.status_{status}")
            if status in {"idle", "starting", "listening", "recognizing", "error"}
            else status
        )
        self._status_label.setText(t("page.voice.summary_status", value=status_text))
        self._language_label.setText(
            t(
                "page.voice.summary_language",
                source=self._language_name(self._runtime["source"]),
                target=self._language_name(self._runtime["target"]),
            )
        )
        self._service_label.setText(
            t("page.voice.summary_service", value=self._runtime["service"])
        )
        self._translator_label.setText(
            t("page.voice.summary_translation", value=self._runtime["translator"])
        )

    def _refresh_recent(self) -> None:
        t = self._i18n.tr
        if not self._last_original and not self._last_translated:
            self._recent_original.setText(t("page.voice.no_recent"))
            self._recent_translation.clear()
            return
        suffix = t("page.voice.partial_suffix") if self._partial else ""
        self._recent_original.setText(
            t("page.voice.recent_original", text=f"{self._last_original}{suffix}")
            if self._last_original
            else ""
        )
        self._recent_translation.setText(
            t("page.voice.recent_translation", text=self._last_translated)
            if self._last_translated
            else ""
        )

    def _language_name(self, value: str) -> str:
        return next(
            (label for label, code in languages(self._i18n) if code == value),
            value,
        )

    def _target_changed(self, _index: int) -> None:
        if self._loading_targets:
            return
        process_id = self.selected_process_id
        if process_id is not None:
            self.target_selected.emit(process_id)

    def _overlay_edited(self) -> None:
        if self._loading_settings:
            return
        self._refresh_overlay_preview()
        self.overlay_settings_changed.emit(
            self.overlay_topmost_check.isChecked(),
            str(self.display_mode_combo.currentData() or "both"),
            float(self.overlay_opacity_edit.value()) / 100,
            int(self.overlay_font_edit.value()),
            int(self.overlay_items_edit.value()),
        )

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        self._apply_responsive_layout()

    def showEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().showEvent(event)
        self._layout_refresh_timer.start()

    def _apply_responsive_layout(self) -> None:
        viewport_width = self._scroll.viewport().width()
        viewport_width = max(viewport_width, max(0, self.width() - 44))
        narrow = viewport_width < 900
        if narrow == self._narrow_layout:
            return
        self._narrow_layout = narrow
        for card in (
            self._source_card,
            self._summary_card,
            self._recent_card,
            self._overlay_card,
        ):
            self._grid.removeWidget(card)
        if narrow:
            self._grid.addWidget(self._summary_card, 0, 0, 1, 2)
            self._grid.addWidget(self._source_card, 1, 0, 1, 2)
            self._grid.addWidget(self._recent_card, 2, 0, 1, 2)
            self._grid.addWidget(self._overlay_card, 3, 0, 1, 2)
            self._grid.setColumnStretch(0, 1)
            self._grid.setColumnStretch(1, 0)
            self._choice_layout.setDirection(QBoxLayout.Direction.TopToBottom)
            self._form_and_preview.setDirection(
                QBoxLayout.Direction.TopToBottom
            )
        else:
            self._grid.addWidget(self._summary_card, 0, 0)
            self._grid.addWidget(self._source_card, 0, 1)
            self._grid.addWidget(self._recent_card, 1, 0, 1, 2)
            self._grid.addWidget(self._overlay_card, 2, 0, 1, 2)
            self._grid.setColumnStretch(0, 1)
            self._grid.setColumnStretch(1, 1)
            self._choice_layout.setDirection(QBoxLayout.Direction.LeftToRight)
            self._form_and_preview.setDirection(
                QBoxLayout.Direction.LeftToRight
            )

    def _refresh_overlay_preview(self) -> None:
        if not hasattr(self, "_preview_original"):
            return
        mode = str(self.display_mode_combo.currentData() or "both")
        show_original = mode in {"original", "both"}
        show_translation = mode in {"translation", "both"}
        self._preview_original.setVisible(show_original)
        self._preview_translation.setVisible(show_translation)
        try:
            opacity = float(self.overlay_opacity_edit.value()) / 100
            font_size = int(self.overlay_font_edit.value())
        except ValueError:
            return
        self._preview_opacity_effect.setOpacity(opacity)
        self._preview_original.setFont(
            font_with_pixel_height(
                self._preview_original,
                self._preview_original.font(),
                font_size
                if mode == "original"
                else max(10, round(font_size * 0.76)),
            )
        )
        self._preview_translation.setFont(
            font_with_pixel_height(
                self._preview_translation,
                self._preview_translation.font(),
                font_size,
            )
        )
