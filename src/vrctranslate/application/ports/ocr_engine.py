from __future__ import annotations

from typing import Protocol

from vrctranslate.domain.ocr import OcrText


class OcrEngine(Protocol):
    def recognize(self, frame: object) -> list[OcrText]: ...

