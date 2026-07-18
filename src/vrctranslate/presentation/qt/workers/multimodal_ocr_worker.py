from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from uuid import uuid4

from PySide6.QtCore import QThread, Signal

from vrctranslate.application.dto import OcrSettings, TranslationProfile, TranslationRouteSettings
from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.ports.ocr_engine import OcrEngine
from vrctranslate.application.ports.visual_frame_encoder import VisualFrameEncoder
from vrctranslate.application.use_cases.translate_visual_frame import TranslateVisualFrame
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.ocr import CaptureRegion, OcrText
from vrctranslate.domain.text_rules import frame_signature_changed
from vrctranslate.domain.visual_translation import VisualTextRegion, VisualTranslationRequest, VisualTranslationResult


@dataclass(frozen=True, slots=True)
class MultimodalOcrOutcome:
    result: VisualTranslationResult
    sources: tuple[tuple[str, OcrText], ...] = ()


class MultimodalOcrWorker(QThread):
    status_changed = Signal(str)
    result_ready = Signal(object)
    failed = Signal(str)

    def __init__(
        self,
        capture: FrameCapture,
        ocr_engine: OcrEngine,
        translate_visual: TranslateVisualFrame,
        frame_encoder: VisualFrameEncoder,
        hwnd: int,
        region: CaptureRegion,
        settings: OcrSettings,
        profile: TranslationProfile,
        route: TranslationRouteSettings,
        display_mode: str,
        parent: object = None,
    ) -> None:
        super().__init__(parent)  # type: ignore[arg-type]
        self._capture = capture
        self._ocr_engine = ocr_engine
        self._translate_visual = translate_visual
        self._frame_encoder = frame_encoder
        self._hwnd = hwnd
        self._region = region
        self._settings = deepcopy(settings)
        self._profile = deepcopy(profile)
        self._route = deepcopy(route)
        self._display_mode = display_mode

    def run(self) -> None:
        previous_signature: bytes | None = None
        try:
            while not self.isInterruptionRequested():
                frame = self._capture.capture(self._hwnd, self._region)
                if (
                    self._settings.recognition_mode == "continuous"
                    and not frame_signature_changed(
                        previous_signature,
                        frame.signature,
                        self._settings.change_threshold,
                    )
                ):
                    del frame
                    self.msleep(self._settings.multimodal_interval_ms)
                    continue
                previous_signature = frame.signature
                outcome = self._translate_frame(frame.pixels)
                del frame
                if outcome is not None:
                    self.result_ready.emit(outcome)
                if self._settings.recognition_mode == "single":
                    break
                self.msleep(self._settings.multimodal_interval_ms)
        except VrcTranslateError as exc:
            self.failed.emit(exc.user_message)
        except Exception as exc:
            self.failed.emit(f"多模态 OCR 运行失败：{type(exc).__name__}")
        finally:
            self.status_changed.emit("多模态 OCR 已停止，内存帧已释放")

    def _translate_frame(self, pixels: object) -> MultimodalOcrOutcome | None:
        self.status_changed.emit("正在通过多模态服务识别并翻译框选画面…")
        sources: tuple[tuple[str, OcrText], ...] = ()
        regions: tuple[VisualTextRegion, ...] = ()
        if self._display_mode in {"inline", "both"}:
            detected = [
                item
                for item in self._ocr_engine.detect(pixels)
                if item.confidence >= self._settings.confidence and item.box
            ]
            detected.sort(key=lambda item: (item.box[0][1], item.box[0][0]))
            detected = detected[:64]
            source_items: list[tuple[str, OcrText]] = []
            region_items: list[VisualTextRegion] = []
            for index, source in enumerate(detected, start=1):
                region_id = f"r{index}"
                xs = [point[0] for point in source.box]
                ys = [point[1] for point in source.box]
                source_items.append((region_id, source))
                region_items.append(
                    VisualTextRegion(
                        region_id,
                        (min(xs), min(ys), max(xs), max(ys)),
                    )
                )
            sources = tuple(source_items)
            regions = tuple(region_items)
            if not regions:
                self.status_changed.emit("当前画面没有检测到可嵌字的文字区域")
                return None

        encoded = self._frame_encoder.encode(
            pixels,
            maximum_side=self._settings.multimodal_max_image_side,
            quality=self._settings.multimodal_image_quality,
            regions=regions,
        )
        request = VisualTranslationRequest(
            uuid4().hex,
            encoded.image_bytes,
            encoded.mime_type,
            self._route.source_language,
            self._route.target_language,
            encoded.regions,
        )
        result = self._translate_visual.execute(
            request,
            self._profile,
            glossary_enabled=self._route.glossary_enabled,
        )
        return MultimodalOcrOutcome(result, sources)

    def stop(self) -> None:
        self.requestInterruption()
