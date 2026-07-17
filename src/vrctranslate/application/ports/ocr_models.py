from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol


@dataclass(frozen=True, slots=True)
class OcrModelStatus:
    language: str
    version: str
    installed: bool
    download_size: int
    installed_size: int


class OcrModelManagement(Protocol):
    def statuses(self) -> list[OcrModelStatus]: ...

    def status(self, language: str) -> OcrModelStatus: ...

    def install(self, language: str) -> OcrModelStatus: ...

    def remove(self, language: str) -> OcrModelStatus: ...
