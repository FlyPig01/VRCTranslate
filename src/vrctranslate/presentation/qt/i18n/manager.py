from __future__ import annotations

import json
from importlib.resources import files

from PySide6.QtCore import QObject, Signal

from vrctranslate.domain.languages import INTERFACE_LOCALES


_SUPPORTED_LOCALES = frozenset(item.locale for item in INTERFACE_LOCALES)
_FALLBACK_LOCALE = "en_US"


class I18nManager(QObject):
    language_changed = Signal(str)

    def __init__(self, locale: str = "zh_CN") -> None:
        super().__init__()
        self._locale = locale
        self._strings: dict[str, str] = {}
        self._load(locale)

    @property
    def locale(self) -> str:
        return self._locale

    def tr(self, key: str, **kwargs: object) -> str:
        text = self._strings.get(key, key)
        return text.format(**kwargs) if kwargs else text

    def set_language(self, locale: str) -> None:
        if locale == self._locale:
            return
        self._load(locale)
        self.language_changed.emit(self._locale)

    def _load(self, locale: str) -> None:
        selected = locale if locale in _SUPPORTED_LOCALES else _FALLBACK_LOCALE
        root = files("vrctranslate.presentation.qt.i18n").joinpath("locales")
        fallback = json.loads(
            root.joinpath(f"{_FALLBACK_LOCALE}.json").read_text(encoding="utf-8")
        )
        if selected == _FALLBACK_LOCALE:
            self._strings = fallback
            self._locale = selected
            return
        try:
            localized = json.loads(
                root.joinpath(f"{selected}.json").read_text(encoding="utf-8")
            )
        except (FileNotFoundError, json.JSONDecodeError, OSError):
            self._strings = fallback
            self._locale = _FALLBACK_LOCALE
            return
        self._strings = {**fallback, **localized}
        self._locale = selected
