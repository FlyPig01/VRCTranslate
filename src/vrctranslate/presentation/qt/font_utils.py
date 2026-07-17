from __future__ import annotations

from PySide6.QtGui import QFont
from PySide6.QtWidgets import QWidget


def font_with_pixel_height(widget: QWidget, source: QFont, pixels: int) -> QFont:
    """Return a valid point-sized font approximating the requested pixel height."""

    font = QFont(source)
    dpi = max(1, widget.logicalDpiY())
    font.setPointSizeF(max(1.0, max(1, pixels) * 72.0 / dpi))
    return font
