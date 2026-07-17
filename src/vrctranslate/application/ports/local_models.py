from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol


@dataclass(frozen=True, slots=True)
class LocalTranslationModel:
    source_language: str
    target_language: str
    package_version: str = ""
    size_bytes: int = 0

    @property
    def language_pair(self) -> str:
        return f"{self.source_language} → {self.target_language}"


class LocalModelManager(Protocol):
    @property
    def component_available(self) -> bool: ...

    @property
    def model_directory(self) -> str: ...

    def installed_models(self) -> list[LocalTranslationModel]: ...

    def available_models(self, refresh: bool = False) -> list[LocalTranslationModel]: ...

    def install(
        self,
        source_language: str,
        target_language: str,
        package_version: str = "",
    ) -> None: ...

    def remove(self, source_language: str, target_language: str) -> None: ...

    def disk_usage(self) -> int: ...
