from __future__ import annotations

from typing import Protocol

from vrctranslate.domain.ocr import OcrText


class OcrEngine(Protocol):
    def recognize(self, frame: object) -> list[OcrText]: ...

    def set_source_language(self, source_language: str) -> None: ...
