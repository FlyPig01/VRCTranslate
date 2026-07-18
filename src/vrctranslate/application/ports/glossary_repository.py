from __future__ import annotations

from typing import Protocol
from pathlib import Path

from vrctranslate.domain.glossary import GlossaryEntry


class GlossaryRepository(Protocol):
    @property
    def revision(self) -> int: ...

    def builtin_entries(self) -> tuple[GlossaryEntry, ...]: ...

    def user_entries(self) -> tuple[GlossaryEntry, ...]: ...

    def save_user_entries(self, entries: list[GlossaryEntry]) -> None: ...

    def load_external(self, path: Path) -> tuple[GlossaryEntry, ...]: ...

    def export_external(self, path: Path, entries: list[GlossaryEntry]) -> None: ...
