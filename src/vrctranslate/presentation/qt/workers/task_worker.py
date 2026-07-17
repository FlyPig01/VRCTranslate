from __future__ import annotations

from collections.abc import Callable
from typing import Any

from PySide6.QtCore import QObject, QRunnable, Signal, Slot


class TaskSignals(QObject):
    succeeded = Signal(object)
    failed = Signal(object)
    finished = Signal()


class TaskWorker(QRunnable):
    def __init__(self, task: Callable[[], Any]) -> None:
        super().__init__()
        self._task = task
        self.signals = TaskSignals()

    @Slot()
    def run(self) -> None:
        try:
            result = self._task()
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
