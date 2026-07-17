from __future__ import annotations

from vrctranslate.application.dto import OcrSettings
from vrctranslate.application.ports.ocr_engine import OcrEngine
from vrctranslate.domain.ocr import CapturedFrame, OcrText
from vrctranslate.domain.text_rules import TextDeduplicator, frame_signature_changed


class ProcessOcrFrame:
    def __init__(self, engine: OcrEngine) -> None:
        self._engine = engine
        self._previous_signature: bytes | None = None
        self._deduplicator: TextDeduplicator | None = None
        self._duplicate_seconds: float | None = None

    def execute(self, frame: CapturedFrame, settings: OcrSettings) -> list[OcrText]:
        if not frame_signature_changed(
            self._previous_signature,
            frame.signature,
            settings.change_threshold,
        ):
            return []
        self._previous_signature = frame.signature
        if self._deduplicator is None or self._duplicate_seconds != settings.duplicate_seconds:
            self._deduplicator = TextDeduplicator(settings.duplicate_seconds)
            self._duplicate_seconds = settings.duplicate_seconds
        recognized = self._engine.recognize(frame.pixels)
        return [
            item
            for item in recognized
            if item.confidence >= settings.confidence
            and self._deduplicator.accept(item.text)
        ]

    def reset(self) -> None:
        self._previous_signature = None
        if self._deduplicator:
            self._deduplicator.clear()

