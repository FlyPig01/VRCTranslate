from __future__ import annotations

from vrctranslate.presentation.qt.widgets import NoWheelComboBox

def set_combo(combo: NoWheelComboBox, value: str) -> None:
    index = combo.findData(value)
    if index >= 0:
        combo.setCurrentIndex(index)
