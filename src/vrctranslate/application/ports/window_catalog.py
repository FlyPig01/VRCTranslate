from __future__ import annotations

from typing import Protocol

from vrctranslate.domain.ocr import WindowInfo


class WindowCatalog(Protocol):
    def list_windows(self) -> list[WindowInfo]: ...
