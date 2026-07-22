from __future__ import annotations

from PySide6.QtCore import QSize, Signal
from PySide6.QtGui import QKeySequence
from PySide6.QtWidgets import QHBoxLayout, QKeySequenceEdit, QPushButton, QWidget

from vrctranslate.presentation.qt.icon_resources import load_icon


class ConfirmableHotkeyEdit(QWidget):
    """A shortcut editor that commits only after explicit confirmation."""

    editing_started = Signal()
    editing_finished = Signal()
    shortcut_confirmed = Signal(str)

    def __init__(
        self,
        default_shortcut: str,
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self._default_shortcut = default_shortcut
        self._committed = QKeySequence(default_shortcut)
        self._editing = False

        layout = QHBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(6)

        self.editor = QKeySequenceEdit()
        self.editor.setObjectName("hotkeySequenceEdit")
        self.editor.setMaximumSequenceLength(1)
        self.editor.setClearButtonEnabled(True)
        self.editor.setEnabled(False)
        layout.addWidget(self.editor, 1)

        self.edit_button = self._icon_button("ui/action_edit.svg")
        self.default_button = self._icon_button("ui/action_reset.svg")
        self.cancel_button = self._icon_button("ui/action_cancel.svg")
        self.confirm_button = QPushButton()
        self.confirm_button.setObjectName("hotkeyConfirmButton")
        self.confirm_button.setMinimumWidth(58)
        layout.addWidget(self.edit_button)
        layout.addWidget(self.default_button)
        layout.addWidget(self.cancel_button)
        layout.addWidget(self.confirm_button)

        self.edit_button.clicked.connect(self.begin_edit)
        self.default_button.clicked.connect(self.restore_default)
        self.cancel_button.clicked.connect(self.cancel_edit)
        self.confirm_button.clicked.connect(self.confirm_edit)
        self._sync_mode()

    @staticmethod
    def _icon_button(icon_name: str) -> QPushButton:
        button = QPushButton()
        button.setObjectName("hotkeyIconButton")
        button.setFixedSize(38, 38)
        button.setIcon(load_icon(icon_name))
        button.setIconSize(QSize(18, 18))
        return button

    @property
    def is_editing(self) -> bool:
        return self._editing

    def set_key_sequence(self, shortcut: str) -> None:
        sequence = QKeySequence(shortcut)
        self._committed = sequence
        if not self._editing:
            self.editor.setKeySequence(sequence)

    def shortcut_text(self) -> str:
        return self.editor.keySequence().toString(
            QKeySequence.SequenceFormat.PortableText
        )

    def begin_edit(self) -> None:
        if self._editing:
            return
        self._editing = True
        self.editing_started.emit()
        self._sync_mode()
        self.editor.setFocus()

    def restore_default(self) -> None:
        if self._editing:
            self.editor.setKeySequence(QKeySequence(self._default_shortcut))

    def cancel_edit(self) -> None:
        if not self._editing:
            return
        self.editor.setKeySequence(self._committed)
        self._editing = False
        self._sync_mode()
        self.editing_finished.emit()

    def confirm_edit(self) -> None:
        if not self._editing:
            return
        self._committed = self.editor.keySequence()
        shortcut = self.shortcut_text()
        self._editing = False
        self._sync_mode()
        self.shortcut_confirmed.emit(shortcut)
        self.editing_finished.emit()

    def set_labels(
        self,
        *,
        edit: str,
        restore_default: str,
        cancel: str,
        confirm: str,
    ) -> None:
        self.edit_button.setToolTip(edit)
        self.edit_button.setAccessibleName(edit)
        self.default_button.setToolTip(restore_default)
        self.default_button.setAccessibleName(restore_default)
        self.cancel_button.setToolTip(cancel)
        self.cancel_button.setAccessibleName(cancel)
        self.confirm_button.setText(confirm)
        self.confirm_button.setToolTip(confirm)
        self.confirm_button.setAccessibleName(confirm)

    def _sync_mode(self) -> None:
        self.editor.setEnabled(self._editing)
        self.edit_button.setVisible(not self._editing)
        self.default_button.setVisible(self._editing)
        self.cancel_button.setVisible(self._editing)
        self.confirm_button.setVisible(self._editing)
