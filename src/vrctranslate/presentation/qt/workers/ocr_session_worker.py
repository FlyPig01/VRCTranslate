from __future__ import annotations

from copy import deepcopy

from PySide6.QtCore import QThread, Signal

from vrctranslate.application.dto import OcrSettings
from vrctranslate.application.ports.frame_capture import FrameCapture
from vrctranslate.application.use_cases.process_ocr_frame import ProcessOcrFrame
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.ocr import CaptureRegion


class OcrSessionWorker(QThread):
    status_changed = Signal(str)
    texts_ready = Signal(list)
    failed = Signal(str)

    def __init__(
        self,
        capture: FrameCapture,
        processor: ProcessOcrFrame,
        hwnd: int,
        region: CaptureRegion,
        settings: OcrSettings,
        parent: object = None,
    ) -> None:
        super().__init__(parent)  # type: ignore[arg-type]
        self._capture = capture
        self._processor = processor
        self._hwnd = hwnd
        self._region = region
        self._settings = deepcopy(settings)

    def run(self) -> None:
        self._processor.reset()
        self.status_changed.emit("正在加载本地 OCR 模型…")
        first_frame = True
        frame_count = 0
        try:
            while not self.isInterruptionRequested():
                frame = self._capture.capture(self._hwnd, self._region)
                frame_count += 1
                if first_frame:
                    diagnostics = getattr(self._capture, "diagnostics", "")
                    self.status_changed.emit(
                        "OCR 正在运行；捕获帧仅在内存中处理"
                        + (f"；{diagnostics}" if diagnostics else "")
                    )
                    first_frame = False
                elif frame_count % 10 == 0:
                    diagnostics = getattr(self._capture, "diagnostics", "")
                    if diagnostics:
                        self.status_changed.emit(f"OCR 正在运行；{diagnostics}")
                items = self._processor.execute(frame, self._settings)
                if items:
                    self.texts_ready.emit(items)
                del frame
                self.msleep(self._settings.interval_ms)
        except VrcTranslateError as exc:
            self.failed.emit(exc.user_message)
        except Exception as exc:
            self.failed.emit(f"OCR 运行失败：{type(exc).__name__}")
        finally:
            self._processor.reset()
            self.status_changed.emit("OCR 已停止，内存帧已释放")

    def stop(self) -> None:
        self.requestInterruption()
