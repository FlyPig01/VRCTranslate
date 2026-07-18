from __future__ import annotations

import unicodedata
from dataclasses import dataclass


GLOSSARY_LANGUAGES = frozenset(
    {"any", "zh-CN", "zh-TW", "en", "ja", "ko", "fr", "de", "es", "ru"}
)
GLOSSARY_SCOPES = frozenset({"self", "ocr", "both"})


def normalize_glossary_text(text: str, *, case_sensitive: bool = False) -> str:
    normalized = unicodedata.normalize("NFKC", text).strip()
    return normalized if case_sensitive else normalized.casefold()


@dataclass(frozen=True, slots=True)
class GlossaryEntry:
    id: str
    source_language: str
    target_language: str
    source: str
    target: str
    scope: str = "both"
    case_sensitive: bool = False
    category: str = ""
    notes: str = ""
    builtin: bool = False

    def validate(self) -> None:
        if not self.id.strip():
            raise ValueError("术语 ID 不能为空")
        if self.source_language not in GLOSSARY_LANGUAGES:
            raise ValueError("术语源语言无效")
        if self.target_language not in GLOSSARY_LANGUAGES:
            raise ValueError("术语目标语言无效")
        if not self.source.strip() or not self.target.strip():
            raise ValueError("原文术语和目标术语不能为空")
        if len(self.source) > 160 or len(self.target) > 160:
            raise ValueError("单个术语不能超过 160 个字符")
        if self.scope not in GLOSSARY_SCOPES:
            raise ValueError("术语使用范围无效")

    @property
    def conflict_key(self) -> tuple[str, str, str]:
        return (
            self.source_language,
            self.target_language,
            normalize_glossary_text(self.source),
        )


@dataclass(frozen=True, slots=True)
class GlossaryMatch:
    start: int
    end: int
    source_text: str
    entry: GlossaryEntry


@dataclass(frozen=True, slots=True)
class GlossaryInstruction:
    source: str
    target: str
