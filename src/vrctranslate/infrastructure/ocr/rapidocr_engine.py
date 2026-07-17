from __future__ import annotations

from typing import Any

from vrctranslate.domain.errors import OcrUnavailable
from vrctranslate.domain.ocr import OcrText


class RapidOcrEngine:
    def __init__(self) -> None:
        self._engine: Any = None

    def recognize(self, frame: object) -> list[OcrText]:
        engine = self._get_engine()
        try:
            output = engine(frame)
        except Exception as exc:
            raise OcrUnavailable(f"OCR 识别失败：{type(exc).__name__}") from exc
        result = output[0] if isinstance(output, tuple) else output
        if not result:
            return []
        items: list[OcrText] = []
        for entry in result:
            if not isinstance(entry, (list, tuple)) or len(entry) < 3:
                continue
            box_raw, text_raw, confidence_raw = entry[0], entry[1], entry[2]
            text = str(text_raw).strip()
            try:
                confidence = float(confidence_raw)
            except (TypeError, ValueError):
                continue
            if not text:
                continue
            box: tuple[tuple[int, int], ...] = ()
            try:
                box = tuple((int(point[0]), int(point[1])) for point in box_raw)
            except (TypeError, ValueError, IndexError):
                pass
            items.append(OcrText(text, confidence, box))
        return items

    def _get_engine(self) -> Any:
        if self._engine is not None:
            return self._engine
        try:
            from rapidocr_onnxruntime import RapidOCR

            self._engine = RapidOCR()
        except Exception as exc:
            raise OcrUnavailable(f"无法加载本地 OCR：{type(exc).__name__}") from exc
        return self._engine

