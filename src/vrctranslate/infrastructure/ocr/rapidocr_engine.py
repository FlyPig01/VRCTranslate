from __future__ import annotations

import gc
from typing import Any

import numpy as np
from PIL import Image, ImageFilter

from vrctranslate.domain.errors import OcrUnavailable
from vrctranslate.domain.languages import ocr_package_for_language
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
        self._detection_engine: Any = None
        self._source_language = self._normalize_language(source_language)
        self._model_signature: tuple[tuple[str, int, int], ...] = ()
        self._detection_signature: tuple[str, int, int] | None = None

    @staticmethod
    def _preprocess(pixels: np.ndarray) -> np.ndarray:
        """Sharpen without resizing so OCR boxes stay in captured-frame space."""

        img = Image.fromarray(pixels)
        img = img.filter(ImageFilter.SHARPEN)
        return np.array(img)

    @staticmethod
    def _small_text_retry_frame(pixels: np.ndarray) -> np.ndarray:
        padding = 6
        padded = np.pad(
            pixels,
            ((padding, padding), (padding, padding), (0, 0)),
            mode="edge",
        )
        image = Image.fromarray(padded)
        image = image.resize(
            (image.width * 2, image.height * 2),
            Image.Resampling.BICUBIC,
        )
        return np.array(image.filter(ImageFilter.SHARPEN))

    def recognize(self, frame: object) -> list[OcrText]:
        engine = self._get_engine()
        try:
            output = engine(self._preprocess(frame))
        except Exception as exc:
            raise OcrUnavailable(f"OCR 识别失败：{type(exc).__name__}") from exc
        items = self._recognized_items(output, frame)
        if not self._should_retry_small_text(frame, items):
            return items
        try:
            retry_output = engine(self._small_text_retry_frame(frame))
        except Exception:
            return items
        retry_items = self._recognized_items(
            retry_output,
            frame,
            scale=2.0,
            padding=6,
        )
        return self._prefer_retry(items, retry_items)

    def _recognized_items(
        self,
        output: object,
        frame: np.ndarray,
        *,
        scale: float = 1.0,
        padding: int = 0,
    ) -> list[OcrText]:
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
                    self._map_point(
                        point,
                        scale=scale,
                        padding=padding,
                        width=width,
                        height=height,
                    )
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
    def _map_point(
        point: object,
        *,
        scale: float,
        padding: int,
        width: int,
        height: int,
    ) -> tuple[int, int]:
        x = round(float(point[0]) / scale - padding)  # type: ignore[index]
        y = round(float(point[1]) / scale - padding)  # type: ignore[index]
        return (
            min(max(0, x), max(0, width - 1)),
            min(max(0, y), max(0, height - 1)),
        )

    @staticmethod
    def _should_retry_small_text(
        frame: np.ndarray,
        items: list[OcrText],
    ) -> bool:
        heights = [
            max(point[1] for point in item.box)
            - min(point[1] for point in item.box)
            for item in items
            if item.box
        ]
        if heights:
            return float(np.median(heights)) < 22.0
        height, width = frame.shape[:2]
        return min(height, width) <= 320

    @staticmethod
    def _prefer_retry(
        original: list[OcrText],
        retry: list[OcrText],
    ) -> list[OcrText]:
        if not retry:
            return original
        if not original:
            return retry

        def score(items: list[OcrText]) -> tuple[int, float, int]:
            characters = sum(len(item.text) for item in items)
            confidence = sum(item.confidence for item in items) / len(items)
            return characters, confidence, len(items)

        return retry if score(retry) > score(original) else original

    def ensure_available(self) -> None:
        """Fail before capture starts when the selected language pack is absent."""

        if self._model_manager.signature(self._source_language):
            return
        raise self._missing_model_error()

    def ensure_detection_available(self) -> None:
        if self._model_manager.detection_signature() is not None:
            return
        raise OcrUnavailable(
            "多模态嵌字需要本地文字检测模型。请打开设置 → OCR → 本地 OCR 模型，"
            "安装任意一个 OCR 模型后再试。"
        )

    def detect(self, frame: object) -> list[OcrText]:
        engine = self._get_detection_engine()
        try:
            output = engine(
                self._preprocess(frame),
                use_det=True,
                use_cls=False,
                use_rec=False,
            )
        except Exception as exc:
            raise OcrUnavailable(f"OCR 文字检测失败：{type(exc).__name__}") from exc
        boxes = getattr(output, "boxes", None)
        scores = getattr(output, "scores", None)
        if boxes is None:
            return []
        height, width = frame.shape[:2]
        output_items: list[OcrText] = []
        for index, box_raw in enumerate(boxes):
            try:
                box = tuple(
                    (round(float(point[0])), round(float(point[1])))
                    for point in box_raw
                )
            except (TypeError, ValueError, IndexError):
                continue
            try:
                confidence = float(scores[index]) if scores is not None else 1.0
            except (TypeError, ValueError, IndexError):
                confidence = 1.0
            output_items.append(
                OcrText(
                    "",
                    confidence,
                    box,
                    (box,),
                    (int(width), int(height)),
                    self._background_luminance(frame, box),
                )
            )
        return output_items

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
        self.release_recognition_session()

    def release_recognition_session(self) -> None:
        previous = self._engine
        self._engine = None
        self._model_signature = ()
        if previous is not None:
            del previous
            gc.collect()

    def _get_engine(self) -> Any:
        signature = self._model_manager.signature(self._source_language)
        if self._engine is not None and signature == self._model_signature:
            return self._engine
        if not signature:
            self.release_recognition_session()
            raise self._missing_model_error()
        try:
            from rapidocr import LangDet, LangRec, ModelType, OCRVersion, RapidOCR
        except Exception as exc:
            raise OcrUnavailable(f"无法加载本地 OCR：{type(exc).__name__}") from exc
        paths = self._model_manager.paths(self._source_language)
        rec_lang = {
            "zh-CN": LangRec.CH,
            "ja": LangRec.JAPAN,
            "en": LangRec.EN,
            "ko": LangRec.KOREAN,
            "latin": LangRec.LATIN,
            "cyrillic": LangRec.CYRILLIC,
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
            "Global.min_height": 10,
            "Global.max_side_len": 4000,
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

    def _get_detection_engine(self) -> Any:
        signature = self._model_manager.detection_signature()
        if self._detection_engine is not None and signature == self._detection_signature:
            return self._detection_engine
        if signature is None:
            self.ensure_detection_available()
        try:
            from rapidocr import LangDet, ModelType, OCRVersion, RapidOCR
        except Exception as exc:
            raise OcrUnavailable(f"无法加载本地文字检测：{type(exc).__name__}") from exc
        params = {
            "Global.use_det": True,
            "Global.use_cls": False,
            "Global.use_rec": False,
            "Global.log_level": "warning",
            "Global.min_height": 10,
            "Global.max_side_len": 4000,
            "Global.model_root_dir": str(self._model_manager.models_root),
            "EngineConfig.onnxruntime.intra_op_num_threads": 2,
            "EngineConfig.onnxruntime.inter_op_num_threads": 1,
            "Det.model_path": str(self._model_manager.detection_path()),
            "Det.ocr_version": OCRVersion.PPOCRV5,
            "Det.model_type": ModelType.MOBILE,
            "Det.lang_type": LangDet.CH,
        }
        try:
            self._detection_engine = RapidOCR(params=params)
        except Exception as exc:
            raise OcrUnavailable(f"OCR 文字检测模型加载失败：{type(exc).__name__}") from exc
        self._detection_signature = signature
        return self._detection_engine

    def _missing_model_error(self) -> OcrUnavailable:
        language_name = {
            "zh-CN": "中文",
            "ja": "日文",
            "en": "英文",
            "ko": "韩文",
            "latin": "拉丁文字",
            "cyrillic": "西里尔文字",
        }[self._source_language]
        return OcrUnavailable(
            f"尚未安装{language_name} OCR 模型。请打开设置 → OCR → 本地 OCR 模型，"
            "下载并安装对应模型后再开始识别。"
        )

    @staticmethod
    def _normalize_language(source_language: str) -> str:
        try:
            return ocr_package_for_language(source_language)
        except ValueError as exc:
            raise OcrUnavailable("当前语言没有可用的本地 OCR 模型。") from exc
