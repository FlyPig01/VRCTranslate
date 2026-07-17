import numpy as np

from vrctranslate.infrastructure.ocr.rapidocr_engine import RapidOcrEngine


def test_rapidocr_accepts_an_in_memory_frame() -> None:
    frame = np.zeros((60, 160, 3), dtype=np.uint8)
    result = RapidOcrEngine().recognize(frame)
    assert isinstance(result, list)

