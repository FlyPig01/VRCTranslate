from __future__ import annotations

from PySide6.QtWidgets import QLabel, QVBoxLayout, QWidget

from vrctranslate.presentation.qt.font_utils import font_with_pixel_height
from vrctranslate.presentation.qt.windows.ocr_overlay.content_model import OverlayEntry


class TranslationItem(QWidget):
    def __init__(
        self,
        entry: OverlayEntry,
        font_size: int,
        show_original: bool,
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self._entry = entry
        self._font_size = -1
        self._show_original = not show_original
        self.setObjectName("ocrTranslationItem")
        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 7, 8, 8)
        layout.setSpacing(2)
        self.original_label = QLabel(entry.original, self)
        self.original_label.setObjectName("ocrOriginal")
        self.original_label.setWordWrap(True)
        self.translation_label = QLabel(entry.translated, self)
        self.translation_label.setObjectName("ocrTranslation")
        self.translation_label.setWordWrap(True)
        layout.addWidget(self.original_label)
        layout.addWidget(self.translation_label)
        self.apply_style(font_size, show_original)

    def apply_style(self, font_size: int, show_original: bool) -> None:
        if self._font_size != font_size:
            self.original_label.setFont(
                font_with_pixel_height(
                    self.original_label,
                    self.original_label.font(),
                    max(10, round(font_size * 0.76)),
                )
            )
            self.translation_label.setFont(
                font_with_pixel_height(
                    self.translation_label,
                    self.translation_label.font(),
                    font_size,
                )
            )
            self._font_size = font_size
        if self._show_original != show_original:
            self.original_label.setVisible(
                show_original and bool(self._entry.original)
            )
            self._show_original = show_original
