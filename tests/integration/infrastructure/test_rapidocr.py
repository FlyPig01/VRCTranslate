import numpy as np
import pytest
from pathlib import Path

from vrctranslate.domain.errors import OcrUnavailable
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager
from vrctranslate.infrastructure.ocr.model_manager import OcrModelPaths
from vrctranslate.infrastructure.ocr.rapidocr_engine import RapidOcrEngine


def test_rapidocr_reports_a_missing_portable_model(tmp_path) -> None:
    frame = np.zeros((60, 160, 3), dtype=np.uint8)
    manager = OcrModelManager(tmp_path / "models", tmp_path / "cache")

    with pytest.raises(OcrUnavailable, match="尚未安装中文 OCR 模型"):
        RapidOcrEngine(manager, "zh-CN").recognize(frame)


def test_rapidocr_reports_a_missing_english_model(tmp_path) -> None:
    frame = np.zeros((60, 160, 3), dtype=np.uint8)
    manager = OcrModelManager(tmp_path / "models", tmp_path / "cache")

    with pytest.raises(OcrUnavailable, match="尚未安装英文 OCR 模型"):
        RapidOcrEngine(manager, "en").recognize(frame)


def test_rapidocr_keeps_boxes_in_original_frame_coordinates(monkeypatch) -> None:
    class Models:
        models_root = Path("models")

        def signature(self, _language):
            return (("model", 1, 1),)

        def paths(self, language):
            return OcrModelPaths(
                language,
                Path("det.onnx"),
                Path("cls.onnx"),
                Path("rec.onnx"),
                "PP-OCRv5",
                "server",
            )

    class Output:
        boxes = np.array([[[10, 12], [70, 12], [70, 32], [10, 32]]])
        txts = ("你好",)
        scores = (0.93,)

    class FakeRapidOCR:
        def __init__(self, params):
            self.params = params

        def __call__(self, _frame):
            return Output()

    monkeypatch.setattr("rapidocr.RapidOCR", FakeRapidOCR)
    frame = np.full((100, 200, 3), 32, dtype=np.uint8)

    result = RapidOcrEngine(Models(), "zh-CN").recognize(frame)  # type: ignore[arg-type]

    assert result[0].box == ((10, 12), (70, 12), (70, 32), (10, 32))
    assert result[0].line_boxes == (result[0].box,)
    assert result[0].canvas_size == (200, 100)
    assert result[0].background_luminance == pytest.approx(32 / 255)


def test_rapidocr_uses_english_mobile_recognition_settings(monkeypatch) -> None:
    from rapidocr import LangRec, ModelType, OCRVersion

    captured = {}

    class Models:
        models_root = Path("models")

        def signature(self, _language):
            return (("model", 1, 1),)

        def paths(self, language):
            return OcrModelPaths(
                language,
                Path("det.onnx"),
                Path("cls.onnx"),
                Path("en-rec.onnx"),
                "PP-OCRv5",
                "mobile",
            )

    class Output:
        boxes = np.empty((0, 4, 2))
        txts = ()
        scores = ()

    class FakeRapidOCR:
        def __init__(self, params):
            captured.update(params)

        def __call__(self, _frame):
            return Output()

    monkeypatch.setattr("rapidocr.RapidOCR", FakeRapidOCR)
    RapidOcrEngine(Models(), "en").recognize(  # type: ignore[arg-type]
        np.zeros((60, 160, 3), dtype=np.uint8)
    )

    assert captured["Rec.lang_type"] == LangRec.EN
    assert captured["Rec.ocr_version"] == OCRVersion.PPOCRV5
    assert captured["Rec.model_type"] == ModelType.MOBILE
