from __future__ import annotations

from copy import deepcopy
from uuid import uuid4

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QDialog,
    QDialogButtonBox,
    QFileDialog,
    QFormLayout,
    QGridLayout,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from vrctranslate.application.dto import GlossarySettings
from vrctranslate.domain.glossary import GlossaryEntry
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.widgets import NoWheelComboBox


_LANGUAGE_ORDER = (
    "zh-CN", "zh-TW", "en", "ja", "ko", "fr", "de", "es", "ru"
)
_LANGUAGE_INDEX = {value: index for index, value in enumerate(_LANGUAGE_ORDER)}
_DEFAULT_LANGUAGE_PAIRS = (("zh-CN", "ja"), ("zh-CN", "en"))
_PAIR_SEPARATOR = "\x1f"


def _language_pair(source: str, target: str) -> tuple[str, str] | None:
    if "any" in {source, target} or source == target:
        return None
    return tuple(  # type: ignore[return-value]
        sorted(
            (source, target),
            key=lambda language: _LANGUAGE_INDEX.get(language, 999),
        )
    )


def _pair_key(pair: tuple[str, str]) -> str:
    return _PAIR_SEPARATOR.join(pair)


class GlossaryEntryDialog(QDialog):
    def __init__(
        self,
        i18n: I18nManager,
        entry: GlossaryEntry | None = None,
        language_pair: tuple[str, str] | None = None,
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self._i18n = i18n
        self._entry_id = entry.id if entry else f"user-{uuid4().hex[:12]}"
        self._notes = entry.notes if entry else ""
        self.setWindowTitle(
            i18n.tr("glossary.dialog.edit" if entry else "glossary.dialog.add")
        )
        self.setMinimumWidth(440)
        root = QVBoxLayout(self)
        form = QFormLayout()
        self.source_language = NoWheelComboBox()
        self.target_language = NoWheelComboBox()
        for language in _LANGUAGE_ORDER:
            label = i18n.tr(f"glossary.language.{language.replace('-', '_')}")
            self.source_language.addItem(label, language)
            self.target_language.addItem(label, language)
        self.source = QLineEdit()
        self.target = QLineEdit()
        self.case_sensitive = QCheckBox(i18n.tr("glossary.case_sensitive"))
        self.category = QLineEdit()
        form.addRow(i18n.tr("glossary.source_language"), self.source_language)
        form.addRow(i18n.tr("glossary.target_language"), self.target_language)
        form.addRow(i18n.tr("glossary.source"), self.source)
        form.addRow(i18n.tr("glossary.target"), self.target)
        form.addRow(i18n.tr("glossary.category"), self.category)
        form.addRow("", self.case_sensitive)
        root.addLayout(form)
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Save
            | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self._accept_if_valid)
        buttons.rejected.connect(self.reject)
        root.addWidget(buttons)
        pair = language_pair or _DEFAULT_LANGUAGE_PAIRS[0]
        if entry is not None:
            source_language = entry.source_language
            target_language = entry.target_language
            if "any" in {source_language, target_language}:
                source_language, target_language = pair
            self._set_combo(self.source_language, source_language)
            self._set_combo(self.target_language, target_language)
            self.source.setText(entry.source)
            self.target.setText(entry.target)
            self.case_sensitive.setChecked(entry.case_sensitive)
            self.category.setText(entry.category)
        else:
            source, target = pair
            self._set_combo(self.source_language, source)
            self._set_combo(self.target_language, target)

    @staticmethod
    def _set_combo(combo: NoWheelComboBox, value: str) -> None:
        index = combo.findData(value)
        if index >= 0:
            combo.setCurrentIndex(index)

    def _accept_if_valid(self) -> None:
        try:
            if self.source_language.currentData() == self.target_language.currentData():
                raise ValueError(self._i18n.tr("glossary.same_language"))
            self.entry().validate()
        except ValueError as exc:
            QMessageBox.warning(
                self,
                self._i18n.tr("glossary.validation_title"),
                str(exc),
            )
            return
        self.accept()

    def entry(self) -> GlossaryEntry:
        return GlossaryEntry(
            id=self._entry_id,
            source_language=str(self.source_language.currentData()),
            target_language=str(self.target_language.currentData()),
            source=self.source.text().strip(),
            target=self.target.text().strip(),
            scope="both",
            case_sensitive=self.case_sensitive.isChecked(),
            category=self.category.text().strip(),
            notes=self._notes,
        )


class GlossaryTab(QWidget):
    changed = Signal()
    import_requested = Signal(str)
    export_requested = Signal(str, object)

    def __init__(self, i18n: I18nManager) -> None:
        super().__init__()
        self._i18n = i18n
        self._builtin: list[GlossaryEntry] = []
        self._user: list[GlossaryEntry] = []
        self._build_ui()
        self.retranslate()

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(14, 14, 14, 14)
        root.setSpacing(10)

        switches = QHBoxLayout()
        self.enabled = QCheckBox()
        self.builtin_enabled = QCheckBox()
        switches.addWidget(self.enabled)
        switches.addWidget(self.builtin_enabled)
        switches.addStretch()
        root.addLayout(switches)
        self.summary = QLabel()
        self.summary.setObjectName("glossarySummary")
        self.summary.setWordWrap(True)
        root.addWidget(self.summary)

        direction_row = QHBoxLayout()
        self.direction_label = QLabel()
        self.direction_combo = NoWheelComboBox()
        direction_row.addWidget(self.direction_label)
        direction_row.addWidget(self.direction_combo, 1)
        root.addLayout(direction_row)

        self.lists = QTabWidget()
        self.builtin_table = self._table()
        self.user_table = self._table()
        self.lists.addTab(self.builtin_table, "")
        self.lists.addTab(self.user_table, "")
        root.addWidget(self.lists, 1)

        actions = QGridLayout()
        actions.setHorizontalSpacing(8)
        actions.setVerticalSpacing(8)
        self.add_button = QPushButton()
        self.edit_button = QPushButton()
        self.delete_button = QPushButton()
        self.copy_button = QPushButton()
        self.import_button = QPushButton()
        self.export_button = QPushButton()
        buttons = (
            self.add_button,
            self.edit_button,
            self.delete_button,
            self.copy_button,
            self.import_button,
            self.export_button,
        )
        for index, button in enumerate(buttons):
            button.setMinimumHeight(34)
            actions.addWidget(button, index // 3, index % 3)
            actions.setColumnStretch(index % 3, 1)
        root.addLayout(actions)

        self.enabled.toggled.connect(self.changed.emit)
        self.builtin_enabled.toggled.connect(self.changed.emit)
        self.direction_combo.currentIndexChanged.connect(self._direction_changed)
        self.lists.currentChanged.connect(self._update_actions)
        self.builtin_table.itemSelectionChanged.connect(self._update_actions)
        self.user_table.itemSelectionChanged.connect(self._update_actions)
        self.add_button.clicked.connect(self._add)
        self.edit_button.clicked.connect(self._edit)
        self.delete_button.clicked.connect(self._delete)
        self.copy_button.clicked.connect(self._copy_builtin)
        self.import_button.clicked.connect(self._import)
        self.export_button.clicked.connect(self._export)

    @staticmethod
    def _table() -> QTableWidget:
        table = QTableWidget(0, 4)
        table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        table.setSelectionMode(QTableWidget.SelectionMode.SingleSelection)
        table.setTextElideMode(Qt.TextElideMode.ElideRight)
        table.verticalHeader().setVisible(False)
        header = table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        return table

    def retranslate(self) -> None:
        t = self._i18n.tr
        self.enabled.setText(t("glossary.enabled"))
        self.builtin_enabled.setText(t("glossary.builtin_enabled"))
        self.direction_label.setText(t("glossary.direction"))
        self.lists.setTabText(0, t("glossary.builtin_tab"))
        self.lists.setTabText(1, t("glossary.user_tab"))
        self.add_button.setText(t("glossary.add"))
        self.edit_button.setText(t("glossary.edit"))
        self.delete_button.setText(t("glossary.delete"))
        self.copy_button.setText(t("glossary.copy"))
        self.import_button.setText(t("glossary.import"))
        self.export_button.setText(t("glossary.export"))
        self._rebuild_directions()

    def load(
        self,
        settings: GlossarySettings,
        builtin: tuple[GlossaryEntry, ...],
        user: tuple[GlossaryEntry, ...],
    ) -> None:
        self.enabled.blockSignals(True)
        self.builtin_enabled.blockSignals(True)
        self.enabled.setChecked(settings.enabled)
        self.builtin_enabled.setChecked(settings.builtin_enabled)
        self.enabled.blockSignals(False)
        self.builtin_enabled.blockSignals(False)
        self._builtin = list(deepcopy(builtin))
        self._user = list(deepcopy(user))
        self._rebuild_directions()

    def collect_settings(self, settings: GlossarySettings) -> None:
        settings.enabled = self.enabled.isChecked()
        settings.builtin_enabled = self.builtin_enabled.isChecked()

    def user_entries(self) -> list[GlossaryEntry]:
        return deepcopy(self._user)

    def set_user_entries(self, entries: tuple[GlossaryEntry, ...]) -> None:
        self._user = [self._all_scopes(entry) for entry in deepcopy(entries)]
        self._rebuild_directions()
        self.changed.emit()

    @staticmethod
    def _all_scopes(entry: GlossaryEntry) -> GlossaryEntry:
        if entry.scope == "both":
            return entry
        return GlossaryEntry(
            id=entry.id,
            source_language=entry.source_language,
            target_language=entry.target_language,
            source=entry.source,
            target=entry.target,
            scope="both",
            case_sensitive=entry.case_sensitive,
            category=entry.category,
            notes=entry.notes,
            builtin=entry.builtin,
        )

    def _language_pairs(self) -> list[tuple[str, str]]:
        values = set(_DEFAULT_LANGUAGE_PAIRS)
        values.update(
            pair
            for entry in self._builtin + self._user
            if (pair := _language_pair(entry.source_language, entry.target_language))
            is not None
        )
        return sorted(
            values,
            key=lambda pair: (
                _LANGUAGE_INDEX.get(pair[0], 999),
                _LANGUAGE_INDEX.get(pair[1], 999),
            ),
        )

    def _rebuild_directions(self) -> None:
        current = self._selected_pair()
        self.direction_combo.blockSignals(True)
        self.direction_combo.clear()
        for first, second in self._language_pairs():
            self.direction_combo.addItem(
                self._i18n.tr(
                    "glossary.direction_value",
                    source=self._language(first),
                    target=self._language(second),
                ),
                _pair_key((first, second)),
            )
        index = self.direction_combo.findData(_pair_key(current))
        self.direction_combo.setCurrentIndex(index if index >= 0 else 0)
        self.direction_combo.blockSignals(False)
        self._direction_changed()

    def _selected_pair(self) -> tuple[str, str]:
        value = str(self.direction_combo.currentData() or "")
        parts = value.split(_PAIR_SEPARATOR, 1)
        if len(parts) == 2 and all(parts):
            return parts[0], parts[1]
        return _DEFAULT_LANGUAGE_PAIRS[0]

    def _direction_changed(self, *_: object) -> None:
        first, second = self._selected_pair()
        headers = (
            self._i18n.tr("glossary.index"),
            self._i18n.tr("glossary.language_column", language=self._language(first)),
            self._i18n.tr("glossary.language_column", language=self._language(second)),
            self._i18n.tr("glossary.category"),
        )
        self.builtin_table.setHorizontalHeaderLabels(headers)
        self.user_table.setHorizontalHeaderLabels(headers)
        self._refresh_tables()

    def _visible(self, entry: GlossaryEntry) -> bool:
        pair = set(self._selected_pair())
        concrete_languages = {
            language
            for language in (entry.source_language, entry.target_language)
            if language != "any"
        }
        return not concrete_languages or concrete_languages.issubset(pair)

    def _refresh_tables(self) -> None:
        user_conflicts = {entry.conflict_key for entry in self._user}
        visible_builtin = [entry for entry in self._builtin if self._visible(entry)]
        self._fill_table(
            self.builtin_table,
            self._unique_builtin_rows(visible_builtin),
            user_conflicts,
        )
        self._fill_table(
            self.user_table,
            [entry for entry in self._user if self._visible(entry)],
            user_conflicts,
        )
        overridden = sum(entry.conflict_key in user_conflicts for entry in self._builtin)
        self.summary.setText(
            self._i18n.tr(
                "glossary.summary",
                builtin=len(self._builtin),
                user=len(self._user),
                overridden=overridden,
            )
        )
        self._update_actions()

    def _unique_builtin_rows(
        self,
        entries: list[GlossaryEntry],
    ) -> list[GlossaryEntry]:
        output: list[GlossaryEntry] = []
        seen: set[tuple[str, str, str]] = set()
        for entry in entries:
            first, second = self._entry_terms(entry)
            key = (first.casefold(), second.casefold(), entry.category.casefold())
            if key in seen:
                continue
            seen.add(key)
            output.append(entry)
        return output

    def _fill_table(
        self,
        table: QTableWidget,
        entries: list[GlossaryEntry],
        user_conflicts: set[tuple[str, str, str]],
    ) -> None:
        table.setRowCount(len(entries))
        for row, entry in enumerate(entries):
            first, second = self._entry_terms(entry)
            values = (str(row + 1), first, second, entry.category)
            for column, value in enumerate(values):
                item = QTableWidgetItem(value)
                item.setData(Qt.ItemDataRole.UserRole, entry.id)
                if entry.builtin and entry.conflict_key in user_conflicts:
                    item.setToolTip(self._i18n.tr("glossary.overridden"))
                table.setItem(row, column, item)

    def _entry_terms(self, entry: GlossaryEntry) -> tuple[str, str]:
        first, second = self._selected_pair()
        if entry.source_language == first or entry.target_language == second:
            return entry.source, entry.target
        if entry.source_language == second or entry.target_language == first:
            return entry.target, entry.source
        return entry.source, entry.target

    def _language(self, value: str) -> str:
        return self._i18n.tr(f"glossary.language.{value.replace('-', '_')}")

    @staticmethod
    def _selected_id(table: QTableWidget) -> str:
        row = table.currentRow()
        item = table.item(row, 0) if row >= 0 else None
        return str(item.data(Qt.ItemDataRole.UserRole)) if item else ""

    def _selected(
        self,
        entries: list[GlossaryEntry],
        table: QTableWidget,
    ) -> GlossaryEntry | None:
        selected_id = self._selected_id(table)
        return next((entry for entry in entries if entry.id == selected_id), None)

    def _update_actions(self, *_: object) -> None:
        builtin_page = self.lists.currentIndex() == 0
        self.copy_button.setEnabled(
            builtin_page and self.builtin_table.currentRow() >= 0
        )
        selected_user = not builtin_page and self.user_table.currentRow() >= 0
        self.edit_button.setEnabled(selected_user)
        self.delete_button.setEnabled(selected_user)

    def _add(self) -> None:
        dialog = GlossaryEntryDialog(
            self._i18n,
            language_pair=self._selected_pair(),
            parent=self,
        )
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self._replace_or_append(dialog.entry())

    def _edit(self) -> None:
        entry = self._selected(self._user, self.user_table)
        if entry is None:
            return
        dialog = GlossaryEntryDialog(
            self._i18n,
            entry,
            language_pair=self._selected_pair(),
            parent=self,
        )
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self._replace_or_append(dialog.entry())

    def _replace_or_append(self, entry: GlossaryEntry) -> None:
        entry = self._all_scopes(entry)
        for current in self._user:
            if current.id != entry.id and current.conflict_key == entry.conflict_key:
                QMessageBox.warning(
                    self,
                    self._i18n.tr("glossary.duplicate_title"),
                    self._i18n.tr("glossary.duplicate"),
                )
                return
        self._user = [item for item in self._user if item.id != entry.id]
        self._user.append(entry)
        self._rebuild_directions()
        self.changed.emit()

    def _delete(self) -> None:
        entry = self._selected(self._user, self.user_table)
        if entry is None:
            return
        self._user = [item for item in self._user if item.id != entry.id]
        self._rebuild_directions()
        self.changed.emit()

    def _copy_builtin(self) -> None:
        entry = self._selected(self._builtin, self.builtin_table)
        if entry is None:
            return
        copied = GlossaryEntry(
            id=f"user-{uuid4().hex[:12]}",
            source_language=entry.source_language,
            target_language=entry.target_language,
            source=entry.source,
            target=entry.target,
            scope="both",
            case_sensitive=entry.case_sensitive,
            category=entry.category,
            notes=entry.notes,
        )
        dialog = GlossaryEntryDialog(
            self._i18n,
            copied,
            language_pair=self._selected_pair(),
            parent=self,
        )
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self._replace_or_append(dialog.entry())

    def _import(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self,
            self._i18n.tr("glossary.import"),
            "",
            "JSON (*.json)",
        )
        if path:
            self.import_requested.emit(path)

    def _export(self) -> None:
        path, _ = QFileDialog.getSaveFileName(
            self,
            self._i18n.tr("glossary.export"),
            "user_glossary.json",
            "JSON (*.json)",
        )
        if path:
            self.export_requested.emit(path, self.user_entries())
