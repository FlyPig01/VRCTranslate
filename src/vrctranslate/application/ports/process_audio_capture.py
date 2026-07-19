from __future__ import annotations

from collections.abc import Callable
from typing import Protocol

from vrctranslate.domain.speech import AudioFrame, ProcessAudioCaptureError


class ProcessAudioCapture(Protocol):
    @property
    def running(self) -> bool: ...

    def start(
        self,
        process_id: int,
        on_frame: Callable[[AudioFrame], None],
        *,
        include_process_tree: bool = True,
        on_error: Callable[[ProcessAudioCaptureError], None] | None = None,
    ) -> None: ...

    def stop(self) -> None: ...
