from __future__ import annotations

import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.text_preprocessing.invariants import protect_invariants
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


class _EchoTranslator:
    def __init__(self, *, break_token: bool = False, space_token: bool = False) -> None:
        self.break_token = break_token
        self.space_token = space_token
        self.requests: list[TranslationRequest] = []

    def translate(
        self,
        request: TranslationRequest,
        _profile: TranslationProfile,
    ) -> TranslationResult:
        self.requests.append(request)
        translated = request.text
        if self.break_token:
            translated = translated.replace("VRCKP", "BROKEN")
        elif self.space_token:
            marker = next(part for part in translated.split() if part.startswith("VRCKP"))
            translated = translated.replace(marker, " ".join(marker))
        return TranslationResult(
            request.request_id,
            request.text,
            translated,
            request.source_language,
            request.target_language,
            request.purpose,
        )


def test_protection_round_trip_keeps_vrchat_identifiers_and_numbers() -> None:
    source = (
        "Open https://example.com/world?id=42 and contact "
        "@Player_01 through /chatbox/input at 12.5%. "
        r"Keep `Avatar_01`, v1.2.3 and E:\VRC\config.json unchanged."
    )

    protected = protect_invariants(source)

    assert protected.text != source
    assert "https://" not in protected.text
    assert "Player_01" not in protected.text
    assert protected.restore(protected.text) == source


def test_translate_text_restores_invariants_after_provider_response() -> None:
    translator = _EchoTranslator()
    request = TranslationRequest(
        "invariant",
        "Visit https://example.com/world?id=42 with Player_01",
        "en",
        "zh-CN",
    )

    result = TranslateText(translator).execute(
        request,
        TranslationProfile(provider="test"),
    )

    assert result.translated == request.text
    assert "VRCKP" in translator.requests[0].text


def test_restore_accepts_spaces_inserted_inside_placeholder() -> None:
    translator = _EchoTranslator(space_token=True)
    request = TranslationRequest(
        "spaced",
        "Contact Player_01",
        "en",
        "zh-CN",
    )

    result = TranslateText(translator).execute(
        request,
        TranslationProfile(provider="test"),
    )

    assert result.translated == request.text


def test_damaged_invariant_blocks_translation_result() -> None:
    translator = _EchoTranslator(break_token=True)
    request = TranslationRequest(
        "broken",
        "Contact Player_01 at https://example.com?id=42",
        "en",
        "zh-CN",
    )

    with pytest.raises(TranslationError, match="内容保护校验") as raised:
        TranslateText(translator).execute(
            request,
            TranslationProfile(provider="test"),
        )

    assert raised.value.category == "response"
    assert len(translator.requests) == 1
