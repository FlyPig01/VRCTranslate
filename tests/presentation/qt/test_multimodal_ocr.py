from __future__ import annotations

import numpy as np

from vrctranslate.application.dto import OcrSettings, TranslationProfile, TranslationRouteSettings
from vrctranslate.domain.ocr import CaptureRegion, OcrText
from vrctranslate.domain.visual_translation import (
    EncodedVisualFrame,
    VisualRegionTranslation,
    VisualTranslationResult,
)
from vrctranslate.presentation.qt.workers.multimodal_ocr_worker import MultimodalOcrWorker


class _Capture:
    pass


class _Engine:
    def detect(self, _pixels):
        box = ((10, 12), (100, 12), (100, 36), (10, 36))
        return [OcrText("", 0.95, box, (box,), (200, 100), 0.2)]


class _Encoder:
    def __init__(self) -> None:
        self.regions = ()

    def encode(self, _pixels, *, maximum_side, quality, regions=()):
        assert maximum_side == 1600
        assert quality == 85
        self.regions = regions
        return EncodedVisualFrame(b"image", "image/jpeg", regions)


class _VisualUseCase:
    def __init__(self) -> None:
        self.request = None

    def execute(self, request, profile, *, glossary_enabled=True):
        self.request = request
        assert profile.provider == "multimodal_openai"
        assert glossary_enabled is True
        if request.regions:
            return VisualTranslationResult(
                request.request_id,
                regions=(
                    VisualRegionTranslation("r1", "こんにちは", "你好"),
                ),
            )
        return VisualTranslationResult(request.request_id, "hello", "你好")


def _worker(display_mode: str):
    encoder = _Encoder()
    visual = _VisualUseCase()
    worker = MultimodalOcrWorker(
        _Capture(),  # type: ignore[arg-type]
        _Engine(),  # type: ignore[arg-type]
        visual,  # type: ignore[arg-type]
        encoder,
        1,
        CaptureRegion(0, 0, 200, 100),
        OcrSettings(),
        TranslationProfile(provider="multimodal_openai"),
        TranslationRouteSettings(source_language="ja", target_language="zh-CN"),
        display_mode,
    )
    return worker, encoder, visual


def test_overlay_multimodal_path_skips_local_detection(qtbot) -> None:
    del qtbot
    worker, encoder, visual = _worker("overlay")

    outcome = worker._translate_frame(np.zeros((100, 200, 3), dtype=np.uint8))

    assert outcome is not None
    assert outcome.sources == ()
    assert encoder.regions == ()
    assert visual.request.regions == ()


def test_inline_multimodal_path_maps_detection_boxes_to_stable_ids(qtbot) -> None:
    del qtbot
    worker, encoder, visual = _worker("inline")

    outcome = worker._translate_frame(np.zeros((100, 200, 3), dtype=np.uint8))

    assert outcome is not None
    assert encoder.regions[0].region_id == "r1"
    assert encoder.regions[0].bbox == (10, 12, 100, 36)
    assert visual.request.regions[0].region_id == "r1"
    assert outcome.sources[0][0] == "r1"
