from __future__ import annotations

from vrctranslate.application.dto import OcrSettings
from vrctranslate.application.ports.ocr_engine import OcrEngine
from vrctranslate.application.use_cases.ocr.spatial_text_tracker import (
    SpatialTextTracker,
)
from vrctranslate.application.use_cases.ocr.text_composer import compose_ocr_texts
from vrctranslate.domain.ocr import CapturedFrame, OcrText
from vrctranslate.domain.text_rules import frame_signature_changed


class ProcessOcrFrame:
    def __init__(self, engine: OcrEngine) -> None:
        self._engine = engine
        self._previous_signature: bytes | None = None
        self._text_tracker = SpatialTextTracker()

    def execute(self, frame: CapturedFrame, settings: OcrSettings) -> list[OcrText]:
        if settings.recognition_mode == "continuous":
            if not frame_signature_changed(
                self._previous_signature,
                frame.signature,
                settings.change_threshold,
            ):
                return []
        self._previous_signature = frame.signature
        recognized = self._engine.recognize(frame.pixels)
        accepted = [
            item for item in recognized if item.confidence >= settings.confidence
        ]
        if settings.recognition_mode == "continuous":
            accepted = self._text_tracker.changed(accepted)
        return compose_ocr_texts(accepted)

    def reset(self) -> None:
        self._previous_signature = None
        self._text_tracker.clear()
