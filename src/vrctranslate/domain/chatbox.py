from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum


class MessageFormat(StrEnum):
    TRANSLATION_ONLY = "translation_only"
    ORIGINAL_THEN_TRANSLATION = "original_then_translation"
    TRANSLATION_THEN_ORIGINAL = "translation_then_original"


@dataclass(frozen=True, slots=True)
class PreparedChatboxMessage:
    text: str
    used_units: int
    maximum_units: int

    @property
    def exceeds_limit(self) -> bool:
        return self.used_units > self.maximum_units


@dataclass(frozen=True, slots=True)
class ChatboxQueueItem:
    item_id: str
    text: str


@dataclass(frozen=True, slots=True)
class ChatboxDrainResult:
    item_id: str
    sent: bool
    error_message: str = ""

