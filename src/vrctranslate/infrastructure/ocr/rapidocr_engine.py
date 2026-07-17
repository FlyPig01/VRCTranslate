from __future__ import annotations

import os
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image, ImageFilter

from vrctranslate.domain.errors import OcrUnavailable
from vrctranslate.domain.ocr import OcrText

_JAPANESE_MODEL_FILENAME = "PP-OCRv4_rec/japan_PP-OCRv4_rec_infer.onnx"


def _get_models_dir() -> Path:
    import rapidocr_onnxruntime

    return Path(rapidocr_onnxruntime.__file__).parent / "models"


class RapidOcrEngine:
    """Thin wrapper around RapidOCR that switches recognition model by source language."""

    def __init__(self, source_language: str = "zh") -> None:
        self._engine: Any = None
        self._source_language = source_language

    @staticmethod
    def _preprocess(pixels: np.ndarray) -> np.ndarray:
        img = Image.fromarray(pixels)
        img = img.filter(ImageFilter.SHARPEN)
        w, h = img.size
        img = img.resize((int(w * 1.5), int(h * 1.5)), Image.LANCZOS)
        return np.array(img)

    def recognize(self, frame: object) -> list[OcrText]:
        engine = self._get_engine()
        try:
            output = engine(self._preprocess(frame))
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

    def set_source_language(self, source_language: str) -> None:
        if source_language == self._source_language:
            return
        self._source_language = source_language
        self._engine = None  # Force rebuild with new model

    def _get_engine(self) -> Any:
        if self._engine is not None:
            return self._engine
        try:
            from rapidocr_onnxruntime import RapidOCR
        except Exception as exc:
            raise OcrUnavailable(f"无法加载本地 OCR：{type(exc).__name__}") from exc

        kwargs: dict[str, str] = {}
        if self._source_language in ("ja", "auto"):
            model_path = _get_models_dir() / _JAPANESE_MODEL_FILENAME
            model_path = Path(os.path.normpath(model_path))
            if model_path.exists():
                kwargs["rec_model_path"] = str(model_path)

        self._engine = RapidOCR(
            box_thresh=0.4,
            unclip_ratio=2.0,
            **kwargs,
        )
        return self._engine

