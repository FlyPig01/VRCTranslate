from __future__ import annotations

from PySide6.QtCore import QLocale, Qt
from PySide6.QtGui import QDoubleValidator, QIntValidator, QKeyEvent, QWheelEvent
from PySide6.QtWidgets import QLineEdit


class NumericLineEdit(QLineEdit):
    """A directly editable number without any implicit stepping behaviour."""

    def __init__(
        self,
        minimum: int | float,
        maximum: int | float,
        decimals: int = 0,
        parent=None,
    ) -> None:
        super().__init__(parent)
        self.minimum = minimum
        self.maximum = maximum
        self.decimals = decimals
        if decimals:
            # The validator restricts syntax only. Range validation is delayed
            # until focus loss/save so an out-of-range value remains visible
            # and editable instead of being silently rejected or clamped.
            validator = QDoubleValidator(-1.0e100, 1.0e100, decimals, self)
            validator.setNotation(QDoubleValidator.Notation.StandardNotation)
            validator.setLocale(QLocale.c())
        else:
            validator = QIntValidator(-2147483647, 2147483647, self)
        self.setValidator(validator)
        self.editingFinished.connect(self.validate_value)

    def setValue(self, value: int | float) -> None:
        if self.decimals:
            text = f"{float(value):.{self.decimals}f}".rstrip("0").rstrip(".")
        else:
            text = str(int(value))
        self.setText(text)
        self._set_invalid(False)

    def value(self) -> int | float:
        if not self.validate_value():
            kind = "整数" if not self.decimals else f"最多 {self.decimals} 位小数"
            raise ValueError(
                f"请输入 {self.minimum} 到 {self.maximum} 之间的数字（{kind}）"
            )
        return float(self.text()) if self.decimals else int(self.text())

    def validate_value(self) -> bool:
        valid = self.hasAcceptableInput() and bool(self.text().strip())
        if valid:
            parsed = float(self.text()) if self.decimals else int(self.text())
            valid = self.minimum <= parsed <= self.maximum
        self._set_invalid(not valid)
        return valid

    def _set_invalid(self, invalid: bool) -> None:
        if self.property("invalid") == invalid:
            return
        self.setProperty("invalid", invalid)
        self.style().unpolish(self)
        self.style().polish(self)

    def wheelEvent(self, event: QWheelEvent) -> None:
        event.ignore()

    def keyPressEvent(self, event: QKeyEvent) -> None:
        if event.key() in (Qt.Key.Key_Up, Qt.Key.Key_Down):
            event.ignore()
            return
        super().keyPressEvent(event)
