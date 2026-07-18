from __future__ import annotations

import json
from datetime import datetime
from importlib.resources import files
from pathlib import Path
from typing import Any

from vrctranslate.domain.glossary import GlossaryEntry


class JsonGlossaryRepository:
    """Read bundled defaults and atomically persist portable user terms."""

    def __init__(self, user_path: Path) -> None:
        self._user_path = user_path
        self._builtin: tuple[GlossaryEntry, ...] | None = None
        self._user: tuple[GlossaryEntry, ...] | None = None
        self._revision = 1

    @property
    def revision(self) -> int:
        return self._revision

    @property
    def user_path(self) -> Path:
        return self._user_path

    def builtin_entries(self) -> tuple[GlossaryEntry, ...]:
        if self._builtin is None:
            resource = files("vrctranslate.infrastructure.glossary.resources").joinpath(
                "default_glossary.json"
            )
            raw = json.loads(resource.read_text(encoding="utf-8"))
            self._builtin = self._parse_builtin(raw)
        return self._builtin

    @classmethod
    def _parse_builtin(cls, raw: object) -> tuple[GlossaryEntry, ...]:
        if isinstance(raw, dict) and isinstance(raw.get("categories"), list):
            return cls._parse_categories(raw)
        if isinstance(raw, dict) and raw.get("version") == 2:
            return cls._parse_concepts(raw)
        return cls._parse_entries(raw, builtin=True)

    @classmethod
    def _parse_categories(cls, raw: dict[str, Any]) -> tuple[GlossaryEntry, ...]:
        language_keys = ("en", "zh", "ja")
        if raw.get("languages") != list(language_keys):
            raise ValueError("分类默认术语表必须完整包含英语、中文和日语")
        categories = raw.get("categories")
        if not isinstance(categories, list):
            raise ValueError("默认术语表 categories 必须是数组")

        language_codes = {"en": "en", "zh": "zh-CN", "ja": "ja"}
        entries: list[GlossaryEntry] = []
        conflicts: set[tuple[str, str, str]] = set()
        for category_index, raw_category in enumerate(categories, start=1):
            if not isinstance(raw_category, dict):
                raise ValueError("默认术语分类必须是对象")
            category = str(raw_category.get("category_name", "")).strip()
            raw_terms = raw_category.get("terms")
            if not category or not isinstance(raw_terms, list):
                raise ValueError("默认术语分类名称为空或 terms 无效")
            for term_index, raw_term in enumerate(raw_terms, start=1):
                if not isinstance(raw_term, dict):
                    raise ValueError("默认术语条目必须是对象")
                terms = {
                    key: cls._category_term_values(raw_term.get(key), key)
                    for key in language_keys
                }
                notes = str(raw_term.get("note", "")).strip()
                for source_key in language_keys:
                    for target_key in language_keys:
                        if source_key == target_key:
                            continue
                        source_language = language_codes[source_key]
                        target_language = language_codes[target_key]
                        target = terms[target_key][0]
                        for alias_index, source in enumerate(terms[source_key]):
                            entry = GlossaryEntry(
                                id=(
                                    f"builtin-c{category_index}-t{term_index}-"
                                    f"{source_language}-{target_language}-{alias_index}"
                                ),
                                source_language=source_language,
                                target_language=target_language,
                                source=source,
                                target=target,
                                category=category,
                                notes=notes,
                                builtin=True,
                            )
                            entry.validate()
                            if entry.conflict_key in conflicts:
                                continue
                            conflicts.add(entry.conflict_key)
                            entries.append(entry)
        return tuple(entries)

    @staticmethod
    def _category_term_values(raw_value: object, language: str) -> tuple[str, ...]:
        if not isinstance(raw_value, str) or not raw_value.strip():
            raise ValueError(f"默认术语缺少 {language} 词语")
        values = tuple(
            value.strip() for value in raw_value.split(" / ") if value.strip()
        )
        if len({value.casefold() for value in values}) != len(values):
            raise ValueError(f"默认术语 {language} 包含重复别名")
        return values

    @classmethod
    def _parse_concepts(cls, raw: dict[str, Any]) -> tuple[GlossaryEntry, ...]:
        languages = ("zh-CN", "en", "ja")
        if raw.get("languages") != list(languages):
            raise ValueError("默认术语表必须完整包含简体中文、英语和日语")
        concepts = raw.get("concepts")
        if not isinstance(concepts, list):
            raise ValueError("默认术语表 concepts 必须是数组")

        entries: list[GlossaryEntry] = []
        concept_ids: set[str] = set()
        conflicts: set[tuple[str, str, str]] = set()
        for raw_concept in concepts:
            if not isinstance(raw_concept, dict):
                raise ValueError("默认术语概念必须是对象")
            concept_id = str(raw_concept.get("id", "")).strip()
            if not concept_id or concept_id in concept_ids:
                raise ValueError("默认术语概念 ID 为空或重复")
            concept_ids.add(concept_id)
            terms = cls._concept_terms(raw_concept.get("terms"), languages)
            category = str(raw_concept.get("category", "")).strip()
            notes = str(raw_concept.get("notes", "")).strip()
            case_sensitive = bool(raw_concept.get("case_sensitive", False))

            for source_language in languages:
                for target_language in languages:
                    if source_language == target_language:
                        continue
                    target = terms[target_language][0]
                    for alias_index, source in enumerate(terms[source_language]):
                        entry = GlossaryEntry(
                            id=(
                                f"builtin-{concept_id}-{source_language}-"
                                f"{target_language}-{alias_index}"
                            ),
                            source_language=source_language,
                            target_language=target_language,
                            source=source,
                            target=target,
                            case_sensitive=case_sensitive,
                            category=category,
                            notes=notes,
                            builtin=True,
                        )
                        entry.validate()
                        if entry.conflict_key in conflicts:
                            raise ValueError(
                                f"默认术语存在重复源词：{source_language} {source}"
                            )
                        conflicts.add(entry.conflict_key)
                        entries.append(entry)
        return tuple(entries)

    @staticmethod
    def _concept_terms(
        raw_terms: object,
        languages: tuple[str, ...],
    ) -> dict[str, tuple[str, ...]]:
        if not isinstance(raw_terms, dict):
            raise ValueError("默认术语概念 terms 必须是对象")
        result: dict[str, tuple[str, ...]] = {}
        for language in languages:
            raw_values = raw_terms.get(language)
            if not isinstance(raw_values, list) or not raw_values:
                raise ValueError(f"默认术语缺少 {language} 词语")
            values = tuple(str(value).strip() for value in raw_values)
            if any(not value for value in values):
                raise ValueError(f"默认术语 {language} 包含空词语")
            normalized = {value.casefold() for value in values}
            if len(normalized) != len(values):
                raise ValueError(f"默认术语 {language} 包含重复别名")
            result[language] = values
        return result

    def user_entries(self) -> tuple[GlossaryEntry, ...]:
        if self._user is not None:
            return self._user
        if not self._user_path.exists():
            self._user = ()
            return self._user
        try:
            raw = json.loads(self._user_path.read_text(encoding="utf-8"))
            self._user = self._parse_entries(raw, builtin=False)
        except (OSError, ValueError, json.JSONDecodeError):
            timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
            broken = self._user_path.with_name(
                f"{self._user_path.name}.broken-{timestamp}"
            )
            try:
                self._user_path.replace(broken)
            except OSError:
                pass
            self._user = ()
            self._revision += 1
        return self._user

    def save_user_entries(self, entries: list[GlossaryEntry]) -> None:
        validated = tuple(self._validated_user_entries(entries))
        self._user_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self._user_path.with_suffix(self._user_path.suffix + ".tmp")
        payload = {
            "version": 1,
            "entries": [self._entry_to_dict(entry) for entry in validated],
        }
        temporary.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        temporary.replace(self._user_path)
        self._user = validated
        self._revision += 1

    def load_external(self, path: Path) -> tuple[GlossaryEntry, ...]:
        raw = json.loads(path.read_text(encoding="utf-8"))
        entries = self._parse_entries(raw, builtin=False)
        return tuple(self._validated_user_entries(list(entries)))

    def export_external(self, path: Path, entries: list[GlossaryEntry]) -> None:
        validated = tuple(self._validated_user_entries(entries))
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(
            json.dumps(
                {
                    "version": 1,
                    "entries": [self._entry_to_dict(entry) for entry in validated],
                },
                ensure_ascii=False,
                indent=2,
            ),
            encoding="utf-8",
        )
        temporary.replace(path)

    @staticmethod
    def _entry_to_dict(entry: GlossaryEntry) -> dict[str, object]:
        return {
            "id": entry.id,
            "source_language": entry.source_language,
            "target_language": entry.target_language,
            "source": entry.source,
            "target": entry.target,
            "case_sensitive": entry.case_sensitive,
            "category": entry.category,
            "notes": entry.notes,
        }

    @classmethod
    def _parse_entries(
        cls,
        raw: object,
        *,
        builtin: bool,
    ) -> tuple[GlossaryEntry, ...]:
        if not isinstance(raw, dict) or not isinstance(raw.get("entries"), list):
            raise ValueError("术语文件格式无效")
        entries: list[GlossaryEntry] = []
        for item in raw["entries"]:
            if not isinstance(item, dict):
                raise ValueError("术语条目必须是对象")
            entry = cls._entry_from_dict(item, builtin=builtin)
            entry.validate()
            entries.append(entry)
        return tuple(entries)

    @staticmethod
    def _entry_from_dict(raw: dict[str, Any], *, builtin: bool) -> GlossaryEntry:
        return GlossaryEntry(
            id=str(raw.get("id", "")),
            source_language=str(raw.get("source_language", "any")),
            target_language=str(raw.get("target_language", "any")),
            source=str(raw.get("source", "")),
            target=str(raw.get("target", "")),
            scope="both",
            case_sensitive=bool(raw.get("case_sensitive", False)),
            category=str(raw.get("category", "")),
            notes=str(raw.get("notes", "")),
            builtin=builtin,
        )

    @staticmethod
    def _validated_user_entries(
        entries: list[GlossaryEntry],
    ) -> list[GlossaryEntry]:
        ids: set[str] = set()
        conflicts: set[tuple[str, str, str]] = set()
        output: list[GlossaryEntry] = []
        for entry in entries:
            normalized = GlossaryEntry(
                id=entry.id.strip(),
                source_language=entry.source_language,
                target_language=entry.target_language,
                source=entry.source.strip(),
                target=entry.target.strip(),
                scope="both",
                case_sensitive=entry.case_sensitive,
                category=entry.category.strip(),
                notes=entry.notes.strip(),
                builtin=False,
            )
            normalized.validate()
            if normalized.id in ids:
                raise ValueError("用户术语 ID 重复")
            if normalized.conflict_key in conflicts:
                raise ValueError("存在重复的用户术语")
            ids.add(normalized.id)
            conflicts.add(normalized.conflict_key)
            output.append(normalized)
        return output
