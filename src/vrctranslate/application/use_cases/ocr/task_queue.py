from __future__ import annotations

from threading import BoundedSemaphore


class BoundedTaskQueue:
    """Non-blocking capacity gate used by an OCR translation session."""

    def __init__(self, capacity: int) -> None:
        self._slots = BoundedSemaphore(max(1, capacity))

    def try_acquire(self) -> bool:
        return self._slots.acquire(blocking=False)

    def release(self) -> None:
        self._slots.release()
