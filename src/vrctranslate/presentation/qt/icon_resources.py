from __future__ import annotations

from importlib.resources import files

from PySide6.QtGui import QIcon


_RESOURCE_PACKAGE = "vrctranslate.presentation.qt"
_ICON_DIRECTORY = ("resources", "icons")


def icon_path(filename: str) -> str:
    resource = files(_RESOURCE_PACKAGE).joinpath(*_ICON_DIRECTORY, filename)
    return str(resource)


def load_icon(filename: str) -> QIcon:
    return QIcon(icon_path(filename))

