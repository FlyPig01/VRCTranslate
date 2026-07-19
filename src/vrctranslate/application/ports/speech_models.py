from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Protocol


SpeechModelProgress = Callable[[int, int], None]


@dataclass(frozen=True, slots=True)
class SpeechModelStatus:
    model_id: str
    version: str
    installed: bool
    download_size: int
    installed_size: int
    required_download_size: int
    models_root: Path
    removal_pending: bool = False


@dataclass(frozen=True, slots=True)
class SpeechModelPaths:
    model: Path
    tokens: Path
    runtime_root: Path


class SpeechModelManagement(Protocol):
    def status(self) -> SpeechModelStatus: ...

    def install(
        self,
        progress: SpeechModelProgress | None = None,
    ) -> SpeechModelStatus: ...

    def remove(self) -> SpeechModelStatus: ...

    def paths(self) -> SpeechModelPaths: ...

    def verify(self) -> SpeechModelPaths: ...
