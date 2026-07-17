from __future__ import annotations

from PySide6.QtCore import QObject, QTimer, Signal

from vrctranslate.application.dto import OcrSettings
from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.use_cases.process_ocr_frame import ProcessOcrFrame
from vrctranslate.domain.ocr import CaptureRegion, OcrText
from vrctranslate.presentation.qt.workers.ocr_session_worker import OcrSessionWorker


class OcrCaptureSession(QObject):
    """Own the capture worker lifetime and expose framework-safe signals."""

    status_changed = Signal(str)
    texts_ready = Signal(list)
    failed = Signal(str)
    finished = Signal()

    def __init__(
        self,
        capture: FrameCapture,
        processor: ProcessOcrFrame,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._capture = capture
        self._processor = processor
        self._worker: OcrSessionWorker | None = None
        self._pending_start = False

    @property
    def is_running(self) -> bool:
        return self._pending_start or bool(self._worker and self._worker.isRunning())

    def start(
        self,
        hwnd: int,
        region: CaptureRegion,
        settings: OcrSettings,
        delayed: bool = False,
    ) -> None:
        self._worker = OcrSessionWorker(
            self._capture, self._processor, hwnd, region, settings, self
        )
        self._worker.status_changed.connect(self.status_changed)
        self._worker.texts_ready.connect(self.texts_ready)
        self._worker.failed.connect(self.failed)
        self._worker.finished.connect(self._worker_finished)
        if delayed:
            self._pending_start = True
            QTimer.singleShot(180, self._start_pending)
        else:
            self._worker.start()

    def _start_pending(self) -> None:
        self._pending_start = False
        if self._worker is not None:
            self._worker.start()

    def stop(self) -> None:
        was_pending = self._pending_start
        self._pending_start = False
        if self._worker is not None and self._worker.isRunning():
            self._worker.stop()
        elif was_pending:
            self._worker = None
            self.finished.emit()

    def _worker_finished(self) -> None:
        self._worker = None
        # Let queued texts_ready deliveries reach the coordinator before a
        # single-shot session is finalized and its translation scheduler stops.
        QTimer.singleShot(0, self.finished.emit)

    def shutdown(self, timeout_ms: int) -> bool:
        self._pending_start = False
        worker = self._worker
        if worker and worker.isRunning():
            worker.stop()
            return worker.wait(timeout_ms)
        self._worker = None
        return True
