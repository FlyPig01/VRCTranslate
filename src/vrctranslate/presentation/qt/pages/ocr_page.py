from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QBoxLayout,
    QCheckBox,
    QComboBox,
    QFormLayout,
    QFrame,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import AppSettings, UiSettings
from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.presentation.qt.font_utils import font_with_pixel_height
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.widgets import NoWheelComboBox, NumericLineEdit
from vrctranslate.presentation.qt.windows.ocr_overlay.surface import OverlaySurface


class OcrPage(QWidget):
    target_selected = Signal(int)
    refresh_targets_requested = Signal()
    ui_settings_changed = Signal()
    overlay_preview_changed = Signal(float, int, bool)
    overlay_show_requested = Signal()
    overlay_reset_requested = Signal()

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._loading_settings = False
        self._loading_targets = False
        self._overlay_geometry = (-1, -1, 420, 220)
        self._last_original = ""
        self._last_translated = ""
        self._narrow_layout: bool | None = None
        self._runtime = {
            "status": "",
            "mode": "continuous",
            "language": "",
            "profile": "",
        }
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
        self._status_title = self._card_title(status_layout)
        self._status_label = QLabel()
        self._status_label.setObjectName("statusPill")
        self._status_label.setWordWrap(True)
        self._mode_label = QLabel()
        self._language_label = QLabel()
        self._profile_label = QLabel()
        for widget in (
            self._status_label,
            self._mode_label,
            self._language_label,
            self._profile_label,
        ):
            widget.setWordWrap(True)
            status_layout.addWidget(widget)
        status_layout.addStretch()
        layout.addWidget(self._status_card, 0, 0)

        self._tool_card, tool_layout = self._card()
        self._tool_title = self._card_title(tool_layout)
        self._target_label = QLabel()
        target_row = QHBoxLayout()
        target_row.setSpacing(8)
        self.target_combo = NoWheelComboBox()
        self.target_combo.setSizeAdjustPolicy(
            QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon
        )
        self.target_combo.setMinimumContentsLength(18)
        self.target_combo.setMinimumWidth(0)
        self.target_combo.setSizePolicy(
            QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed
        )
        self.refresh_targets_button = QPushButton()
        self.refresh_targets_button.setIcon(load_icon("ui/action_refresh.svg"))
        target_row.addWidget(self.target_combo, 1)
        target_row.addWidget(self.refresh_targets_button)
        self.orb_topmost_check = QCheckBox()
        tool_layout.addWidget(self._target_label)
        tool_layout.addLayout(target_row)
        tool_layout.addSpacing(4)
        tool_layout.addWidget(self.orb_topmost_check)
        tool_layout.addStretch()
        layout.addWidget(self._tool_card, 0, 1)

        self._recent_card, recent_layout = self._card()
        self._recent_title = self._card_title(recent_layout)
        self._recent_surface = QFrame()
        self._recent_surface.setObjectName("previewCard")
        recent_surface_layout = QVBoxLayout(self._recent_surface)
        recent_surface_layout.setContentsMargins(14, 11, 14, 12)
        recent_surface_layout.setSpacing(6)
        self._recent_original = QLabel()
        self._recent_original.setObjectName("previewOriginal")
        self._recent_original.setWordWrap(True)
        self._recent_translation = QLabel()
        self._recent_translation.setObjectName("previewTranslated")
        self._recent_translation.setWordWrap(True)
        recent_surface_layout.addWidget(self._recent_original)
        recent_surface_layout.addWidget(self._recent_translation)
        recent_layout.addWidget(self._recent_surface)
        layout.addWidget(self._recent_card, 1, 0, 1, 2)

        self._overlay_card, overlay_layout = self._card()
        self._overlay_title = self._card_title(overlay_layout)
        display_form = QFormLayout()
        display_form.setFieldGrowthPolicy(
            QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow
        )
        self.display_mode_combo = NoWheelComboBox()
        self.inline_opacity_spin = NumericLineEdit(50, 100)
        self._display_mode_label = QLabel()
        self._inline_opacity_label = QLabel()
        display_form.addRow(self._display_mode_label, self.display_mode_combo)
        display_form.addRow(self._inline_opacity_label, self.inline_opacity_spin)
        overlay_layout.addLayout(display_form)
        choices = QWidget()
        choices.setObjectName("settingsChoicePanel")
        self.overlay_choice_panel = choices
        self._choice_layout = QBoxLayout(QBoxLayout.Direction.LeftToRight, choices)
        self._choice_layout.setContentsMargins(12, 8, 12, 8)
        self.ocr_topmost_check = QCheckBox()
        self.ocr_passthrough_check = QCheckBox()
        self.ocr_show_original_check = QCheckBox()
        self.inline_auto_contrast_check = QCheckBox()
        self._choice_layout.addWidget(self.ocr_topmost_check)
        self._choice_layout.addWidget(self.ocr_passthrough_check)
        self._choice_layout.addWidget(self.ocr_show_original_check)
        self._choice_layout.addWidget(self.inline_auto_contrast_check)
        self._choice_layout.addStretch()
        overlay_layout.addWidget(choices)

        form = QFormLayout()
        form.setHorizontalSpacing(14)
        form.setVerticalSpacing(8)
        form.setFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.AllNonFixedFieldsGrow)
        self.overlay_opacity_spin = NumericLineEdit(25, 100)
        self.overlay_font_spin = NumericLineEdit(10, 40)
        self.overlay_items_spin = NumericLineEdit(1, 20)
        self._opacity_label = QLabel()
        self._font_label = QLabel()
        self._items_label = QLabel()
        form.addRow(self._opacity_label, self.overlay_opacity_spin)
        form.addRow(self._font_label, self.overlay_font_spin)
        form.addRow(self._items_label, self.overlay_items_spin)

        self._form_and_preview = QBoxLayout(QBoxLayout.Direction.LeftToRight)
        self._form_and_preview.setSpacing(14)
        self._form_and_preview.addLayout(form, 1)
        self.overlay_style_preview = OverlaySurface()
        self.overlay_style_preview.setObjectName("overlayStylePreview")
        self.overlay_style_preview.setMinimumSize(230, 100)
        preview_layout = QVBoxLayout(self.overlay_style_preview)
        preview_layout.setContentsMargins(14, 10, 14, 10)
        preview_item = QWidget()
        preview_item.setObjectName("overlayPreviewItem")
        item_layout = QVBoxLayout(preview_item)
        item_layout.setContentsMargins(10, 7, 10, 8)
        item_layout.setSpacing(2)
        self._preview_original = QLabel("Hello, world!")
        self._preview_original.setObjectName("ocrOriginal")
        self._preview_translation = QLabel("你好，世界！")
        self._preview_translation.setObjectName("ocrTranslation")
        item_layout.addWidget(self._preview_original)
        item_layout.addWidget(self._preview_translation)
        preview_layout.addWidget(preview_item)
        self._form_and_preview.addWidget(self.overlay_style_preview, 1)
        overlay_layout.addLayout(self._form_and_preview)

        self.overlay_geometry_summary = QLabel()
        self.overlay_geometry_summary.setObjectName("pageSubtitle")
        self.overlay_geometry_summary.setWordWrap(True)
        overlay_layout.addWidget(self.overlay_geometry_summary)
        buttons = QHBoxLayout()
        self.show_overlay_button = QPushButton()
        self.reset_overlay_button = QPushButton()
        self.show_overlay_button.clicked.connect(self.overlay_show_requested)
        self.reset_overlay_button.clicked.connect(self.overlay_reset_requested)
        buttons.addWidget(self.show_overlay_button)
        buttons.addWidget(self.reset_overlay_button)
        buttons.addStretch()
        overlay_layout.addLayout(buttons)
        layout.addWidget(self._overlay_card, 2, 0, 1, 2)

        layout.setColumnStretch(0, 1)
        layout.setColumnStretch(1, 1)
        layout.setRowStretch(3, 1)
        self._scroll.setWidget(self._content)
        root.addWidget(self._scroll, 1)
        self._apply_responsive_layout()

        self.refresh_targets_button.clicked.connect(self.refresh_targets_requested)
        self.target_combo.currentIndexChanged.connect(self._target_edited)
        for check in (
            self.orb_topmost_check,
            self.ocr_topmost_check,
            self.ocr_passthrough_check,
            self.ocr_show_original_check,
            self.inline_auto_contrast_check,
        ):
            check.checkStateChanged.connect(self._settings_edited)
        for edit in (
            self.overlay_opacity_spin,
            self.overlay_font_spin,
            self.overlay_items_spin,
            self.inline_opacity_spin,
        ):
            edit.textChanged.connect(self._settings_edited)
        self.display_mode_combo.currentIndexChanged.connect(self._settings_edited)

    @staticmethod
    def _card() -> tuple[QFrame, QVBoxLayout]:
        card = QFrame()
        card.setObjectName("card")
        layout = QVBoxLayout(card)
        layout.setContentsMargins(18, 15, 18, 15)
        layout.setSpacing(8)
        return card, layout

    @staticmethod
    def _card_title(layout: QVBoxLayout) -> QLabel:
        title = QLabel()
        title.setObjectName("cardTitle")
        layout.addWidget(title)
        return title

    def _retranslate(self) -> None:
        t = self._i18n.tr
        self._title.setText(t("page.ocr.title"))
        self._subtitle.setText(t("page.ocr.subtitle_new"))
        self._status_title.setText(t("page.ocr.status_title"))
        self._tool_title.setText(t("page.ocr.tool_title"))
        self._target_label.setText(t("page.ocr.target_program"))
        self.refresh_targets_button.setText(t("page.ocr.refresh_targets"))
        self.orb_topmost_check.setText(t("page.ocr.orb_topmost"))
        self._recent_title.setText(t("page.ocr.recent_title"))
        self._overlay_title.setText(t("page.ocr.overlay_title"))
        self.ocr_topmost_check.setText(t("ocr_settings.topmost"))
        self.ocr_passthrough_check.setText(t("ocr_settings.passthrough"))
        self.ocr_show_original_check.setText(t("ocr_settings.show_original"))
        self._display_mode_label.setText(t("ocr_display.mode"))
        self._inline_opacity_label.setText(t("ocr_display.inline_opacity"))
        self.inline_auto_contrast_check.setText(t("ocr_display.auto_contrast"))
        self._rebuild_display_modes()
        self._opacity_label.setText(t("ocr_settings.opacity"))
        self._font_label.setText(t("ocr_settings.font_size"))
        self._items_label.setText(t("ocr_settings.max_items"))
        self.show_overlay_button.setText(t("ocr_settings.show_overlay"))
        self.reset_overlay_button.setText(t("ocr_settings.reset_overlay"))
        self._refresh_runtime_labels()
        self._refresh_recent_translation()
        self._update_geometry_summary()

    def _rebuild_display_modes(self) -> None:
        current = self.display_mode_combo.currentData()
        self.display_mode_combo.blockSignals(True)
        self.display_mode_combo.clear()
        for key, value in (
            ("ocr_display.overlay", "overlay"),
            ("ocr_display.inline", "inline"),
            ("ocr_display.both", "both"),
        ):
            self.display_mode_combo.addItem(self._i18n.tr(key), value)
        index = self.display_mode_combo.findData(current)
        self.display_mode_combo.setCurrentIndex(index if index >= 0 else 0)
        self.display_mode_combo.blockSignals(False)

    @property
    def has_unsaved_changes(self) -> bool:
        return False

    @property
    def selected_hwnd(self) -> int | None:
        value = self.target_combo.currentData()
        return int(value) if value is not None else None

    def set_target_windows(
        self, windows: list[WindowInfo], selected_hwnd: int | None
    ) -> None:
        self._loading_targets = True
        try:
            self.target_combo.clear()
            if not windows:
                self.target_combo.addItem(self._i18n.tr("page.ocr.no_targets"), None)
            else:
                selected_exists = selected_hwnd is not None and any(
                    window.hwnd == selected_hwnd for window in windows
                )
                if not selected_exists:
                    self.target_combo.addItem(
                        self._i18n.tr("page.ocr.select_target"), None
                    )
                for window in windows:
                    self.target_combo.addItem(window.display_name, window.hwnd)
                if selected_exists:
                    index = self.target_combo.findData(selected_hwnd)
                    if index >= 0:
                        self.target_combo.setCurrentIndex(index)
        finally:
            self._loading_targets = False

    def load_settings(self, settings: AppSettings) -> None:
        ui = settings.ui
        self._loading_settings = True
        try:
            self.orb_topmost_check.setChecked(ui.ocr_orb_topmost)
            self.ocr_topmost_check.setChecked(ui.ocr_topmost)
            self.ocr_passthrough_check.setChecked(ui.ocr_mouse_passthrough)
            self.ocr_show_original_check.setChecked(ui.ocr_overlay_show_original)
            mode_index = self.display_mode_combo.findData(ui.ocr_display_mode)
            self.display_mode_combo.setCurrentIndex(mode_index if mode_index >= 0 else 0)
            self.inline_opacity_spin.setValue(round(ui.ocr_inline_opacity * 100))
            self.inline_auto_contrast_check.setChecked(ui.ocr_inline_auto_contrast)
            self.overlay_opacity_spin.setValue(round(ui.ocr_overlay_opacity * 100))
            self.overlay_font_spin.setValue(ui.ocr_overlay_font_size)
            self.overlay_items_spin.setValue(ui.ocr_overlay_max_items)
            self.set_overlay_geometry(
                ui.ocr_overlay_x,
                ui.ocr_overlay_y,
                ui.ocr_overlay_width,
                ui.ocr_overlay_height,
            )
        finally:
            self._loading_settings = False
        self._emit_overlay_preview()

    def collect_ui_settings(self, ui: UiSettings) -> None:
        ui.ocr_orb_topmost = self.orb_topmost_check.isChecked()
        ui.ocr_topmost = self.ocr_topmost_check.isChecked()
        ui.ocr_mouse_passthrough = self.ocr_passthrough_check.isChecked()
        ui.ocr_overlay_show_original = self.ocr_show_original_check.isChecked()
        ui.ocr_display_mode = str(self.display_mode_combo.currentData() or "overlay")
        ui.ocr_inline_opacity = float(self.inline_opacity_spin.value()) / 100
        ui.ocr_inline_auto_contrast = self.inline_auto_contrast_check.isChecked()
        ui.ocr_overlay_opacity = float(self.overlay_opacity_spin.value()) / 100
        ui.ocr_overlay_font_size = int(self.overlay_font_spin.value())
        ui.ocr_overlay_max_items = int(self.overlay_items_spin.value())

    def set_status(self, message: str) -> None:
        self._runtime["status"] = message
        self._refresh_runtime_labels()

    def set_runtime_summary(
        self,
        *,
        target: str | None = None,
        mode: str | None = None,
        language: str | None = None,
        profile: str | None = None,
    ) -> None:
        del target
        for key, value in (
            ("mode", mode),
            ("language", language),
            ("profile", profile),
        ):
            if value is not None:
                self._runtime[key] = value
        self._refresh_runtime_labels()

    def set_last_translation(self, original: str, translated: str) -> None:
        self._last_original = original
        self._last_translated = translated
        self._refresh_recent_translation()

    def _refresh_recent_translation(self) -> None:
        t = self._i18n.tr
        if self._last_original or self._last_translated:
            self._recent_original.setText(
                t("page.ocr.recent_original", text=self._last_original)
            )
            self._recent_translation.setText(
                t("page.ocr.recent_translation", text=self._last_translated)
            )
        else:
            self._recent_original.setText(t("page.ocr.no_recent"))
            self._recent_translation.clear()

    def _refresh_runtime_labels(self) -> None:
        t = self._i18n.tr
        self._status_label.setText(
            t("page.ocr.summary_status", value=self._runtime["status"] or t("page.ocr.idle"))
        )
        mode_key = "page.ocr.mode_single" if self._runtime["mode"] == "single" else "page.ocr.mode_continuous"
        self._mode_label.setText(t("page.ocr.summary_mode", value=t(mode_key)))
        self._language_label.setText(
            t("page.ocr.summary_language", value=self._runtime["language"] or "-")
        )
        self._profile_label.setText(
            t("page.ocr.summary_profile", value=self._runtime["profile"] or "-")
        )

    def set_overlay_geometry(self, x: int, y: int, width: int, height: int) -> None:
        self._overlay_geometry = (x, y, width, height)
        self._update_geometry_summary()

    def mark_overlay_geometry_changed(
        self, x: int, y: int, width: int, height: int
    ) -> None:
        self.set_overlay_geometry(x, y, width, height)
        if not self._loading_settings:
            self.ui_settings_changed.emit()

    def _update_geometry_summary(self) -> None:
        x, y, width, height = self._overlay_geometry
        position = self._i18n.tr("ocr_settings.position_auto") if x < 0 or y < 0 else f"x={x}, y={y}"
        self.overlay_geometry_summary.setText(
            self._i18n.tr(
                "ocr_settings.geometry_summary",
                position=position,
                width=width,
                height=height,
            )
        )

    def _target_edited(self, _index: int) -> None:
        if self._loading_targets:
            return
        hwnd = self.selected_hwnd
        if hwnd is not None:
            self.target_selected.emit(hwnd)

    def _settings_edited(self, *_: object) -> None:
        if self._loading_settings:
            return
        self._emit_overlay_preview()
        self.ui_settings_changed.emit()

    def _emit_overlay_preview(self) -> None:
        try:
            opacity = int(self.overlay_opacity_spin.value()) / 100
            font_size = int(self.overlay_font_spin.value())
        except ValueError:
            return
        show_original = self.ocr_show_original_check.isChecked()
        self.overlay_style_preview.set_background_opacity(opacity)
        self._preview_original.setVisible(show_original)
        self._preview_original.setFont(
            font_with_pixel_height(
                self._preview_original,
                self._preview_original.font(),
                max(10, round(font_size * 0.76)),
            )
        )
        self._preview_translation.setFont(
            font_with_pixel_height(
                self._preview_translation,
                self._preview_translation.font(),
                font_size,
            )
        )
        if not self._loading_settings:
            self.overlay_preview_changed.emit(opacity, font_size, show_original)

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        self._apply_responsive_layout()

    def _apply_responsive_layout(self) -> None:
        """Keep every OCR control usable at the main window's minimum size."""

        viewport_width = self._scroll.viewport().width()
        if viewport_width <= 0:
            viewport_width = max(0, self.width() - 44)
        narrow = viewport_width < 900
        if narrow == self._narrow_layout:
            return
        self._narrow_layout = narrow

        layout = self._content_layout
        for card in (
            self._status_card,
            self._tool_card,
            self._recent_card,
            self._overlay_card,
        ):
            layout.removeWidget(card)

        if narrow:
            layout.addWidget(self._status_card, 0, 0, 1, 2)
            layout.addWidget(self._tool_card, 1, 0, 1, 2)
            layout.addWidget(self._recent_card, 2, 0, 1, 2)
            layout.addWidget(self._overlay_card, 3, 0, 1, 2)
            self._form_and_preview.setDirection(QBoxLayout.Direction.TopToBottom)
            self._choice_layout.setDirection(QBoxLayout.Direction.TopToBottom)
        else:
            layout.addWidget(self._status_card, 0, 0)
            layout.addWidget(self._tool_card, 0, 1)
            layout.addWidget(self._recent_card, 1, 0, 1, 2)
            layout.addWidget(self._overlay_card, 2, 0, 1, 2)
            self._form_and_preview.setDirection(QBoxLayout.Direction.LeftToRight)
            self._choice_layout.setDirection(QBoxLayout.Direction.LeftToRight)

    # Compatibility no-ops for older call sites. The diagnostic table remains removed.
    def set_region(self, _settings: object) -> None:
        return

    def set_running(self, _running: bool) -> None:
        return

    def set_stopping(self) -> None:
        return

    def add_recognition(self, *_: object) -> None:
        return

    def set_translation(self, *_: object) -> None:
        return
