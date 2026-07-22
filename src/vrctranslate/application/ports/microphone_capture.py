from __future__ import annotations

from collections.abc import Callable
from typing import Protocol

from vrctranslate.domain.speech import (
    AudioFrame,
    MicrophoneCaptureError,
    MicrophoneDevice,
)


class MicrophoneCapture(Protocol):
    @property
    def running(self) -> bool: ...

    def list_devices(self) -> list[MicrophoneDevice]: ...

    def resolve_device_id(self, device_id: str) -> str: ...

    def start(
        self,
        device_id: str,
        on_frame: Callable[[AudioFrame], None],
        *,
        on_error: Callable[[MicrophoneCaptureError], None] | None = None,
    ) -> None: ...

    def stop(self) -> None: ...
