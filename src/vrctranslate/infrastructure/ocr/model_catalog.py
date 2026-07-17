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
}
