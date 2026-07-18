from __future__ import annotations

from PySide6.QtCore import QObject, Signal

from vrctranslate.application.dto import OcrSettings, TranslationProfile, TranslationRouteSettings
from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.ports.ocr_engine import OcrEngine
from vrctranslate.application.ports.visual_frame_encoder import VisualFrameEncoder
from vrctranslate.application.use_cases.translate_visual_frame import TranslateVisualFrame
from vrctranslate.domain.ocr import CaptureRegion
from vrctranslate.presentation.qt.workers.multimodal_ocr_worker import MultimodalOcrWorker


class MultimodalOcrSession(QObject):
    status_changed = Signal(str)
    result_ready = Signal(object)
    failed = Signal(str)
    finished = Signal()

    def __init__(
        self,
        capture: FrameCapture,
        ocr_engine: OcrEngine,
        translate_visual: TranslateVisualFrame,
        frame_encoder: VisualFrameEncoder,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._capture = capture
        self._ocr_engine = ocr_engine
        self._translate_visual = translate_visual
        self._frame_encoder = frame_encoder
        self._worker: MultimodalOcrWorker | None = None

    @property
    def is_running(self) -> bool:
        return bool(self._worker and self._worker.isRunning())

    def start(
        self,
        hwnd: int,
        region: CaptureRegion,
        settings: OcrSettings,
        profile: TranslationProfile,
        route: TranslationRouteSettings,
        display_mode: str,
    ) -> None:
        worker = MultimodalOcrWorker(
            self._capture,
            self._ocr_engine,
            self._translate_visual,
            self._frame_encoder,
            hwnd,
            region,
            settings,
            profile,
            route,
            display_mode,
            self,
        )
        self._worker = worker
        worker.status_changed.connect(self.status_changed)
        worker.result_ready.connect(self.result_ready)
        worker.failed.connect(self.failed)
        worker.finished.connect(self._worker_finished)
        worker.start()

    def stop(self) -> None:
        if self._worker is not None and self._worker.isRunning():
            self._worker.stop()

    def _worker_finished(self) -> None:
        self._worker = None
        self.finished.emit()

    def shutdown(self, timeout_ms: int) -> bool:
        worker = self._worker
        if worker is not None and worker.isRunning():
            worker.stop()
            return worker.wait(timeout_ms)
        self._worker = None
        return True
