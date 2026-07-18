from __future__ import annotations

import json

from vrctranslate.application.use_cases.glossary import match_glossary
from vrctranslate.domain.glossary import GlossaryEntry
from vrctranslate.infrastructure.glossary.json_repository import (
    JsonGlossaryRepository,
)


def _user_entry() -> GlossaryEntry:
    return GlossaryEntry(
        "user-avatar",
        "ja",
        "zh-CN",
        "アバター",
        "模型",
        "both",
        False,
        "VRChat",
        "custom",
    )


def test_builtin_glossary_is_bundled_and_read_only(tmp_path) -> None:
    repository = JsonGlossaryRepository(
        tmp_path / "data" / "glossaries" / "user_glossary.json"
    )

    entries = repository.builtin_entries()

    assert entries
    assert all(entry.builtin for entry in entries)
    assert any(entry.source == "Quick Menu" for entry in entries)


def test_builtin_glossary_expands_curated_chinese_english_japanese_terms(
    tmp_path,
) -> None:
    repository = JsonGlossaryRepository(tmp_path / "user_glossary.json")
    entries = repository.builtin_entries()
    by_key = {
        (entry.source_language, entry.target_language, entry.source): entry.target
        for entry in entries
    }

    assert {
        entry.source_language for entry in entries
    } | {
        entry.target_language for entry in entries
    } == {"zh-CN", "en", "ja"}
    assert len(entries) == len({entry.conflict_key for entry in entries})
    assert by_key[("en", "zh-CN", "Quick Menu")] == "快捷菜单"
    assert by_key[("en", "ja", "Quick Menu")] == "クイックメニュー"
    assert by_key[("ja", "zh-CN", "クイックメニュー")] == "快捷菜单"
    assert by_key[("zh-CN", "en", "表情菜单")] == "Expression Menu"
    assert by_key[("en", "zh-CN", "Expression Menu")] == "表情菜单"
    assert by_key[("en", "zh-CN", "Instance")] == "房间"


def test_builtin_glossary_uses_supplied_fixed_and_community_terms(tmp_path) -> None:
    repository = JsonGlossaryRepository(tmp_path / "user_glossary.json")
    by_key = {
        (entry.source_language, entry.target_language, entry.source): entry.target
        for entry in repository.builtin_entries()
    }

    assert by_key[("en", "zh-CN", "Contact")] == "接触检测"
    assert by_key[("en", "zh-CN", "Master")] == "房主"
    assert by_key[("en", "ja", "Mirror Dweller")] == "ミラー住人"
    assert by_key[("en", "ja", "Sleepover")] == "寝落ち"
    assert by_key[("ja", "en", "VR寝")] == "Sleepover"
    assert by_key[("en", "zh-CN", "VRC+")] == "VRC+ 会员"
    assert by_key[("zh-CN", "en", "VRC+ 会员")] == "VRChat Plus"
    assert by_key[("en", "zh-CN", "FBT")] == "全身追踪"
    assert by_key[("zh-CN", "en", "全身追踪")] == "Full-Body Tracking"


def test_builtin_multilingual_terms_match_explicit_and_auto_sources(tmp_path) -> None:
    repository = JsonGlossaryRepository(tmp_path / "user_glossary.json")
    entries = repository.builtin_entries()

    english = match_glossary("Quick Menu", entries, "en", "ja", "self")
    japanese = match_glossary(
        "クイックメニュー",
        entries,
        "auto",
        "zh-CN",
        "ocr",
    )

    assert [(match.source_text, match.entry.target) for match in english] == [
        ("Quick Menu", "クイックメニュー")
    ]
    assert [(match.source_text, match.entry.target) for match in japanese] == [
        ("クイックメニュー", "快捷菜单")
    ]


def test_builtin_concept_schema_requires_all_three_languages() -> None:
    raw = {
        "version": 2,
        "languages": ["zh-CN", "en", "ja"],
        "concepts": [
            {
                "id": "broken",
                "terms": {
                    "zh-CN": ["术语"],
                    "en": ["Term"],
                },
            }
        ],
    }

    try:
        JsonGlossaryRepository._parse_builtin(raw)
    except ValueError as exc:
        assert "ja" in str(exc)
    else:
        raise AssertionError("incomplete built-in concept was accepted")


def test_user_glossary_round_trip_is_portable_json(tmp_path) -> None:
    path = tmp_path / "data" / "glossaries" / "user_glossary.json"
    repository = JsonGlossaryRepository(path)

    repository.save_user_entries([_user_entry()])

    raw = json.loads(path.read_text(encoding="utf-8"))
    assert raw["version"] == 1
    assert raw["entries"][0]["target"] == "模型"
    assert "scope" not in raw["entries"][0]
    loaded = JsonGlossaryRepository(path).user_entries()
    assert loaded == (_user_entry(),)


def test_broken_user_glossary_is_preserved_and_returns_empty(tmp_path) -> None:
    path = tmp_path / "data" / "glossaries" / "user_glossary.json"
    path.parent.mkdir(parents=True)
    path.write_text("{broken", encoding="utf-8")

    repository = JsonGlossaryRepository(path)

    assert repository.user_entries() == ()
    assert list(path.parent.glob("user_glossary.json.broken-*"))


def test_duplicate_user_conflict_is_rejected(tmp_path) -> None:
    repository = JsonGlossaryRepository(tmp_path / "user_glossary.json")
    duplicate = GlossaryEntry(
        "other-id",
        "ja",
        "zh-CN",
        "アバター",
        "虚拟形象",
    )

    try:
        repository.save_user_entries([_user_entry(), duplicate])
    except ValueError as exc:
        assert "重复" in str(exc)
    else:
        raise AssertionError("duplicate glossary conflict was accepted")


def test_user_glossary_can_be_exported_and_imported(tmp_path) -> None:
    repository = JsonGlossaryRepository(tmp_path / "user_glossary.json")
    exported = tmp_path / "exports" / "terms.json"

    repository.export_external(exported, [_user_entry()])

    assert repository.load_external(exported) == (_user_entry(),)


def test_legacy_scope_is_normalized_to_all_scenarios(tmp_path) -> None:
    path = tmp_path / "legacy.json"
    path.write_text(
        json.dumps(
            {
                "version": 1,
                "entries": [
                    {
                        "id": "legacy-ocr",
                        "source_language": "ja",
                        "target_language": "zh-CN",
                        "source": "アバター",
                        "target": "模型",
                        "scope": "ocr",
                    }
                ],
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    entry = JsonGlossaryRepository(tmp_path / "user.json").load_external(path)[0]

    assert entry.scope == "both"
