from __future__ import annotations

import json
from importlib.resources import files

from PySide6.QtCore import QObject, Signal


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
        self.language_changed.emit(locale)

    def _load(self, locale: str) -> None:
        resource = files("vrctranslate.presentation.qt.i18n").joinpath(
            "locales", f"{locale}.json"
        )
        self._strings = json.loads(resource.read_text(encoding="utf-8"))
        self._locale = locale
