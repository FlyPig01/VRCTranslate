from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class OcrModelFile:
    relative_path: str
    url: str
    sha256: str
    size: int


@dataclass(frozen=True, slots=True)
class OcrModelPackage:
    language: str
    version: str
    recognition_version: str
    recognition_type: str
    files: tuple[OcrModelFile, ...]

    @property
    def download_size(self) -> int:
        return sum(item.size for item in self.files)


_BASE = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.1/onnx"

DETECTION = OcrModelFile(
    "shared/detection.onnx",
    f"{_BASE}/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx",
    "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae",
    4_819_576,
)

ORIENTATION = OcrModelFile(
    "shared/orientation.onnx",
    f"{_BASE}/PP-OCRv4/cls/ch_ppocr_mobile_v2.0_cls_mobile.onnx",
    "e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c",
    585_532,
)

OCR_MODEL_PACKAGES: dict[str, OcrModelPackage] = {
    "zh-CN": OcrModelPackage(
        "zh-CN",
        "PP-OCRv5-server",
        "PP-OCRv5",
        "server",
        (
            DETECTION,
            ORIENTATION,
            OcrModelFile(
                "zh/recognition.onnx",
                f"{_BASE}/PP-OCRv5/rec/ch_PP-OCRv5_rec_server.onnx",
                "e09385400eaaaef34ceff54aeb7c4f0f1fe014c27fa8b9905d4709b65746562a",
                84_577_022,
            ),
        ),
    ),
    "ja": OcrModelPackage(
        "ja",
        "PP-OCRv6-medium",
        "PP-OCRv6",
        "medium",
        (
            DETECTION,
            ORIENTATION,
            OcrModelFile(
                "ja/recognition.onnx",
                f"{_BASE}/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx",
                "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b",
                76_629_984,
            ),
        ),
    ),
    "en": OcrModelPackage(
        "en",
        "PP-OCRv5-mobile",
        "PP-OCRv5",
        "mobile",
        (
            DETECTION,
            ORIENTATION,
            OcrModelFile(
                "en/recognition.onnx",
                f"{_BASE}/PP-OCRv5/rec/en_PP-OCRv5_rec_mobile.onnx",
                "c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8",
                7_872_351,
            ),
        ),
    ),
    "ko": OcrModelPackage(
        "ko",
        "PP-OCRv5-mobile-korean",
        "PP-OCRv5",
        "mobile",
        (
            DETECTION,
            ORIENTATION,
            OcrModelFile(
                "ko/recognition.onnx",
                f"{_BASE}/PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile.onnx",
                "cd6e2ea50f6943ca7271eb8c56a877a5a90720b7047fe9c41a2e541a25773c9b",
                13_488_748,
            ),
        ),
    ),
    "latin": OcrModelPackage(
        "latin",
        "PP-OCRv5-mobile-latin",
        "PP-OCRv5",
        "mobile",
        (
            DETECTION,
            ORIENTATION,
            OcrModelFile(
                "latin/recognition.onnx",
                f"{_BASE}/PP-OCRv5/rec/latin_PP-OCRv5_rec_mobile.onnx",
                "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a",
                7_904_513,
            ),
        ),
    ),
    "cyrillic": OcrModelPackage(
        "cyrillic",
        "PP-OCRv5-mobile-cyrillic",
        "PP-OCRv5",
        "mobile",
        (
            DETECTION,
            ORIENTATION,
            OcrModelFile(
                "cyrillic/recognition.onnx",
                f"{_BASE}/PP-OCRv5/rec/cyrillic_PP-OCRv5_rec_mobile.onnx",
                "90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711",
                8_074_092,
            ),
        ),
    ),
}
