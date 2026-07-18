from __future__ import annotations

from dataclasses import replace

from vrctranslate.application.dto import AppSettings, TranslationProfile
from vrctranslate.application.use_cases.glossary import (
    match_glossary,
    merge_glossary_entries,
)
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.glossary import GlossaryEntry
from vrctranslate.domain.translation import TranslationRequest, TranslationResult
from vrctranslate.infrastructure.text.wanakana_converter import (
    WanaKanaRomajiConverter,
)


def _entry(
    entry_id: str,
    source: str,
    target: str,
    *,
    source_language: str = "en",
    target_language: str = "zh-CN",
    scope: str = "both",
    builtin: bool = False,
) -> GlossaryEntry:
    return GlossaryEntry(
        entry_id,
        source_language,
        target_language,
        source,
        target,
        scope,
        False,
        "test",
        "",
        builtin,
    )


class MemoryGlossaryRepository:
    def __init__(self, builtin=(), user=()) -> None:
        self.builtin = tuple(builtin)
        self.user = tuple(user)
        self.revision = 1

    def builtin_entries(self):
        return self.builtin

    def user_entries(self):
        return self.user

    def save_user_entries(self, entries):
        self.user = tuple(entries)
        self.revision += 1


class GlossaryTranslator:
    def __init__(self, mode: str, *, break_first_placeholder: bool = False) -> None:
        self.mode = mode
        self.break_first_placeholder = break_first_placeholder
        self.requests = []

    def glossary_mode(self, _profile) -> str:
        return self.mode

    def translate(self, request, _profile):
        self.requests.append(request)
        text = request.text
        if self.break_first_placeholder and len(self.requests) == 1:
            text = text.replace("VRCG", "VRC G")
        return TranslationResult(
            request.request_id,
            request.text,
            f"translated:{text}",
            request.source_language,
            request.target_language,
            request.purpose,
        )


class AlwaysBreakPlaceholderTranslator(GlossaryTranslator):
    def translate(self, request, profile):
        self.requests.append(request)
        text = request.text.replace("VRCG", "VRC G")
        return TranslationResult(
            request.request_id,
            request.text,
            f"translated:{text}",
            request.source_language,
            request.target_language,
            request.purpose,
        )


def test_user_entry_overrides_builtin_conflict() -> None:
    builtin = _entry("default", "avatar", "虚拟形象", builtin=True)
    user = _entry("user", "avatar", "模型")

    merged = merge_glossary_entries((builtin,), (user,), builtin_enabled=True)

    assert merged == (user,)
    assert merge_glossary_entries(
        (builtin,),
        (user,),
        builtin_enabled=False,
    ) == (user,)


def test_longest_term_wins_at_the_same_position() -> None:
    entries = (
        _entry("friend", "friend", "好友"),
        _entry("request", "friend request", "好友请求"),
    )

    matches = match_glossary(
        "friend request sent",
        entries,
        "en",
        "zh-CN",
        "self",
    )

    assert [(item.source_text, item.entry.target) for item in matches] == [
        ("friend request", "好友请求")
    ]


def test_english_term_does_not_match_inside_username_or_longer_word() -> None:
    entries = (_entry("avatar", "avatar", "虚拟形象"),)

    assert not match_glossary(
        "my_avatar avatarEditor",
        entries,
        "en",
        "zh-CN",
        "self",
    )


def test_auto_source_skips_ambiguous_targets() -> None:
    entries = (
        _entry("en", "chat", "聊天", source_language="en"),
        _entry("fr", "chat", "猫", source_language="fr"),
    )

    assert not match_glossary("chat", entries, "auto", "zh-CN", "self")


def test_auto_source_accepts_unique_target_and_kana_script() -> None:
    unique = (
        _entry("en", "avatar", "虚拟形象", source_language="en"),
        _entry("fr", "avatar", "虚拟形象", source_language="fr"),
    )
    kana = (
        _entry("ja", "アバター", "虚拟形象", source_language="ja"),
        _entry("en", "アバター", "错误", source_language="en"),
    )

    assert match_glossary("avatar", unique, "auto", "zh-CN", "self")
    matches = match_glossary("アバター", kana, "auto", "zh-CN", "ocr")
    assert [item.entry.id for item in matches] == ["ja"]


def test_local_placeholder_restores_target_without_exposing_marker() -> None:
    translator = GlossaryTranslator("local_placeholder")
    repository = MemoryGlossaryRepository(
        builtin=(_entry("avatar", "avatar", "虚拟形象", builtin=True),)
    )
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(id="online", provider="deepl")
    ]
    settings.translation.self_route.profile_id = "online"
    use_case = TranslateText(
        translator,
        glossary_repository=repository,
        glossary_settings=lambda: settings.glossary,
    )

    result = use_case.execute(
        TranslationRequest("1", "change avatar", "en", "zh-CN"),
        settings.translation,
    )

    assert result.translated == "translated:change 虚拟形象"
    assert "VRCG" not in result.translated


def test_global_and_route_switches_disable_glossary_without_disabling_translation() -> None:
    translator = GlossaryTranslator("local_placeholder")
    repository = MemoryGlossaryRepository(
        builtin=(_entry("avatar", "avatar", "虚拟形象", builtin=True),)
    )
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(id="online", provider="deepl")
    ]
    settings.translation.self_route.profile_id = "online"
    use_case = TranslateText(
        translator,
        glossary_repository=repository,
        glossary_settings=lambda: settings.glossary,
    )
    request = TranslationRequest("1", "avatar", "en", "zh-CN")

    settings.glossary.enabled = False
    assert use_case.execute(request, settings.translation).translated == "translated:avatar"
    settings.glossary.enabled = True
    settings.translation.self_route.glossary_enabled = False
    assert use_case.execute(request, settings.translation).translated == "translated:avatar"


def test_broken_placeholder_retries_plain_translation() -> None:
    translator = GlossaryTranslator(
        "local_placeholder",
        break_first_placeholder=True,
    )
    repository = MemoryGlossaryRepository(
        builtin=(_entry("avatar", "avatar", "虚拟形象", builtin=True),)
    )
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(id="online", provider="deepl")
    ]
    settings.translation.self_route.profile_id = "online"
    use_case = TranslateText(
        translator,
        glossary_repository=repository,
        glossary_settings=lambda: settings.glossary,
    )

    result = use_case.execute(
        TranslationRequest("1", "change avatar", "en", "zh-CN"),
        settings.translation,
    )

    assert len(translator.requests) == 2
    assert translator.requests[1].text == "change avatar"
    assert result.translated == "translated:change avatar"


def test_repeated_failures_disable_placeholders_for_the_session() -> None:
    translator = AlwaysBreakPlaceholderTranslator("local_placeholder")
    repository = MemoryGlossaryRepository(
        builtin=(_entry("avatar", "avatar", "虚拟形象", builtin=True),)
    )
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(id="online", provider="deepl")
    ]
    settings.translation.self_route.profile_id = "online"
    use_case = TranslateText(
        translator,
        glossary_repository=repository,
        glossary_settings=lambda: settings.glossary,
    )

    for index in range(4):
        result = use_case.execute(
            TranslationRequest(str(index), "avatar", "en", "zh-CN"),
            settings.translation,
        )
        assert result.translated == "translated:avatar"

    assert len(translator.requests) == 7
    assert "VRCG" not in translator.requests[-1].text
    assert use_case.glossary_status("online") == "fallback"


def test_prompt_mode_sends_only_matched_terms_as_structured_data() -> None:
    translator = GlossaryTranslator("prompt")
    repository = MemoryGlossaryRepository(
        builtin=(
            _entry("avatar", "avatar", "虚拟形象", builtin=True),
            _entry("instance", "instance", "实例", builtin=True),
        )
    )
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(id="llm", provider="openai_compatible")
    ]
    settings.translation.self_route.profile_id = "llm"
    use_case = TranslateText(
        translator,
        glossary_repository=repository,
        glossary_settings=lambda: settings.glossary,
    )

    use_case.execute(
        TranslationRequest("1", "change avatar", "en", "zh-CN"),
        settings.translation,
    )

    sent = translator.requests[0]
    assert sent.text == "change avatar"
    assert [(item.source, item.target) for item in sent.glossary] == [
        ("avatar", "虚拟形象")
    ]


def test_user_romaji_term_is_protected_before_romaji_conversion() -> None:
    translator = GlossaryTranslator("local_placeholder")
    repository = MemoryGlossaryRepository(
        user=(
            _entry(
                "romaji",
                "konnichiwa",
                "固定问候",
                source_language="any",
            ),
        )
    )
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(id="online", provider="deepl")
    ]
    settings.translation.self_route.profile_id = "online"
    use_case = TranslateText(
        translator,
        WanaKanaRomajiConverter(),
        repository,
        lambda: settings.glossary,
    )

    result = use_case.execute(
        TranslationRequest("1", "konnichiwa minna", "auto", "zh-CN"),
        settings.translation,
    )

    assert result.translated == "translated:固定问候 みんな"


def test_converted_romaji_can_match_japanese_glossary() -> None:
    translator = GlossaryTranslator("local_placeholder")
    repository = MemoryGlossaryRepository(
        builtin=(
            _entry(
                "hello",
                "こんにちは",
                "你好",
                source_language="ja",
                builtin=True,
            ),
        )
    )
    settings = AppSettings()
    settings.translation.profiles = [
        TranslationProfile(id="online", provider="deepl")
    ]
    settings.translation.self_route.profile_id = "online"
    use_case = TranslateText(
        translator,
        WanaKanaRomajiConverter(),
        repository,
        lambda: settings.glossary,
    )

    result = use_case.execute(
        TranslationRequest("1", "konnichiwa", "auto", "zh-CN"),
        settings.translation,
    )

    assert result.translated == "translated:你好"
