from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Protocol


OcrModelProgress = Callable[[int, int], None]


@dataclass(frozen=True, slots=True)
class OcrModelStatus:
    language: str
    version: str
    installed: bool
    download_size: int
    installed_size: int
    exclusive_size: int
    required_download_size: int


@dataclass(frozen=True, slots=True)
class OcrModelStorage:
    shared_size: int
    total_size: int


class OcrModelManagement(Protocol):
    def statuses(self) -> list[OcrModelStatus]: ...

    def status(self, language: str) -> OcrModelStatus: ...

    def storage(self) -> OcrModelStorage: ...

    def install(
        self,
        language: str,
        progress: OcrModelProgress | None = None,
    ) -> OcrModelStatus: ...

    def remove(self, language: str) -> OcrModelStatus: ...
