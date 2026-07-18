from __future__ import annotations

from collections.abc import Callable
from threading import Event
from typing import Any

from PySide6.QtCore import QObject, QRunnable, Signal, Slot


class _Cancelled(Exception):
    pass


class OcrModelInstallSignals(QObject):
    succeeded = Signal(object)
    failed = Signal(object)
    cancelled = Signal()
    progress = Signal(int, int)
    finished = Signal()


class OcrModelInstallWorker(QRunnable):
    def __init__(
        self,
        install: Callable[[Callable[[int, int], None]], Any],
    ) -> None:
        super().__init__()
        self._install = install
        self._cancelled = Event()
        self.signals = OcrModelInstallSignals()

    def cancel(self) -> None:
        self._cancelled.set()

    def _report_progress(self, completed: int, total: int) -> None:
        if self._cancelled.is_set():
            raise _Cancelled
        self.signals.progress.emit(completed, total)
        if self._cancelled.is_set():
            raise _Cancelled

    @Slot()
    def run(self) -> None:
        try:
            if self._cancelled.is_set():
                raise _Cancelled
            result = self._install(self._report_progress)
        except _Cancelled:
            try:
                self.signals.cancelled.emit()
            except RuntimeError:
                pass
        except Exception as exc:
            try:
                self.signals.failed.emit(exc)
            except RuntimeError:
                pass
        else:
            try:
                self.signals.succeeded.emit(result)
            except RuntimeError:
                pass
        finally:
            try:
                self.signals.finished.emit()
            except RuntimeError:
                pass
