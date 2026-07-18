from __future__ import annotations

from typing import Any

import numpy as np
from PIL import Image, ImageFilter

from vrctranslate.domain.errors import OcrUnavailable
from vrctranslate.domain.ocr import OcrText
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager


class RapidOcrEngine:
    """RapidOCR v3 wrapper using only verified portable model files."""

    def __init__(
        self,
        model_manager: OcrModelManager,
        source_language: str = "zh-CN",
    ) -> None:
        self._model_manager = model_manager
        self._engine: Any = None
        self._source_language = self._normalize_language(source_language)
        self._model_signature: tuple[tuple[str, int, int], ...] = ()

    @staticmethod
    def _preprocess(pixels: np.ndarray) -> np.ndarray:
        """Sharpen without resizing so OCR boxes stay in captured-frame space."""

        img = Image.fromarray(pixels)
        img = img.filter(ImageFilter.SHARPEN)
        return np.array(img)

    def recognize(self, frame: object) -> list[OcrText]:
        engine = self._get_engine()
        try:
            output = engine(self._preprocess(frame))
        except Exception as exc:
            raise OcrUnavailable(f"OCR 识别失败：{type(exc).__name__}") from exc
        boxes = getattr(output, "boxes", None)
        texts = getattr(output, "txts", None)
        scores = getattr(output, "scores", None)
        if boxes is None or texts is None or scores is None:
            return []
        height, width = frame.shape[:2]
        items: list[OcrText] = []
        for box_raw, text_raw, confidence_raw in zip(boxes, texts, scores):
            text = str(text_raw).strip()
            try:
                confidence = float(confidence_raw)
            except (TypeError, ValueError):
                continue
            if not text:
                continue
            box: tuple[tuple[int, int], ...] = ()
            try:
                box = tuple(
                    (round(float(point[0])), round(float(point[1])))
                    for point in box_raw
                )
            except (TypeError, ValueError, IndexError):
                pass
            items.append(
                OcrText(
                    text,
                    confidence,
                    box,
                    (box,) if box else (),
                    (int(width), int(height)),
                    self._background_luminance(frame, box),
                )
            )
        return items

    @staticmethod
    def _background_luminance(
        frame: np.ndarray,
        box: tuple[tuple[int, int], ...],
    ) -> float:
        if not box:
            return 0.5
        height, width = frame.shape[:2]
        xs = [point[0] for point in box]
        ys = [point[1] for point in box]
        left = max(0, min(xs) - 3)
        top = max(0, min(ys) - 3)
        right = min(width, max(xs) + 4)
        bottom = min(height, max(ys) + 4)
        if right <= left or bottom <= top:
            return 0.5
        sample = frame[top:bottom, left:right]
        return max(0.0, min(1.0, float(sample[..., :3].mean()) / 255.0))

    def set_source_language(self, source_language: str) -> None:
        source_language = self._normalize_language(source_language)
        if source_language == self._source_language:
            return
        self._source_language = source_language
        self._engine = None  # Force rebuild with new model

    def _get_engine(self) -> Any:
        signature = self._model_manager.signature(self._source_language)
        if self._engine is not None and signature == self._model_signature:
            return self._engine
        if not signature:
            language_name = {
                "zh-CN": "中文",
                "ja": "日文",
                "en": "英文",
            }[self._source_language]
            raise OcrUnavailable(
                f"尚未安装{language_name} OCR 模型，请在设置 → OCR 中下载。"
            )
        try:
            from rapidocr import LangDet, LangRec, ModelType, OCRVersion, RapidOCR
        except Exception as exc:
            raise OcrUnavailable(f"无法加载本地 OCR：{type(exc).__name__}") from exc
        paths = self._model_manager.paths(self._source_language)
        rec_lang = {
            "zh-CN": LangRec.CH,
            "ja": LangRec.JAPAN,
            "en": LangRec.EN,
        }[self._source_language]
        rec_version = (
            OCRVersion.PPOCRV5
            if paths.recognition_version == "PP-OCRv5"
            else OCRVersion.PPOCRV6
        )
        rec_type = {
            "server": ModelType.SERVER,
            "medium": ModelType.MEDIUM,
            "mobile": ModelType.MOBILE,
        }[paths.recognition_type]
        params = {
            "Global.text_score": 0.0,
            "Global.log_level": "warning",
            "Global.use_cls": False,
            "Global.min_height": 30,
            "Global.max_side_len": 2000,
            "Global.model_root_dir": str(self._model_manager.models_root),
            "EngineConfig.onnxruntime.intra_op_num_threads": 2,
            "EngineConfig.onnxruntime.inter_op_num_threads": 1,
            "Det.model_path": str(paths.detection),
            "Det.ocr_version": OCRVersion.PPOCRV5,
            "Det.model_type": ModelType.MOBILE,
            "Det.lang_type": LangDet.CH,
            "Cls.model_path": str(paths.orientation),
            "Rec.model_path": str(paths.recognition),
            "Rec.ocr_version": rec_version,
            "Rec.model_type": rec_type,
            "Rec.lang_type": rec_lang,
        }
        try:
            self._engine = RapidOCR(params=params)
        except Exception as exc:
            raise OcrUnavailable(f"OCR 模型加载失败：{type(exc).__name__}") from exc
        self._model_signature = signature
        return self._engine

    @staticmethod
    def _normalize_language(source_language: str) -> str:
        if source_language in {"zh", "zh_CN", "zh-CN"}:
            return "zh-CN"
        if source_language in {"ja", "auto"}:
            return "ja"
        if source_language in {"en", "en-US", "en_US"}:
            return "en"
        raise OcrUnavailable("OCR 仅支持中文、日文或英文识别模型。")
