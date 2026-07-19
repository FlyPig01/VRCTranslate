from __future__ import annotations

import httpx
import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.translation.google_free_translator import (
    GoogleFreeTranslator,
)


class _Response:
    def __init__(
        self,
        *,
        status: int = 200,
        data: object | None = None,
        text: str = "",
    ) -> None:
        self.status_code = status
        self._data = data
        self.text = text

    def json(self) -> object:
        if self._data is None:
            raise ValueError("not json")
        return self._data

    def raise_for_status(self) -> None:
        if self.status_code < 400:
            return
        request = httpx.Request("GET", "https://example.invalid")
        response = httpx.Response(self.status_code, request=request)
        raise httpx.HTTPStatusError("failed", request=request, response=response)


class _Client:
    responses: list[_Response] = []
    calls: list[tuple[str, dict[str, str]]] = []

    def __init__(self, *, timeout: float, verify: bool) -> None:
        self.timeout = timeout
        self.verify = verify

    def __enter__(self) -> _Client:
        return self

    def __exit__(self, *_: object) -> bool:
        return False

    def get(
        self,
        endpoint: str,
        *,
        params: dict[str, str],
        headers: dict[str, str],
    ) -> _Response:
        del headers
        type(self).calls.append((endpoint, params))
        return type(self).responses.pop(0)


@pytest.fixture(autouse=True)
def _fake_client(monkeypatch):
    from vrctranslate.infrastructure.translation import google_free_translator

    _Client.responses = []
    _Client.calls = []
    monkeypatch.setattr(google_free_translator.httpx, "Client", _Client)


def _request() -> TranslationRequest:
    return TranslationRequest("google-free", "hello", "auto", "zh-CN", "ocr")


def test_google_free_uses_json_response_when_available() -> None:
    _Client.responses = [
        _Response(data=[[['你好', 'hello', None, None]], None, 'en'])
    ]

    result = GoogleFreeTranslator().translate(
        _request(),
        TranslationProfile(provider="google_free", timeout_seconds=8),
    )

    assert result.translated == "你好"
    assert result.source_language == "en"
    assert [call[0] for call in _Client.calls] == [
        "https://translate.googleapis.com/translate_a/single"
    ]


def test_google_free_falls_back_to_mobile_page_after_rate_limit() -> None:
    _Client.responses = [
        _Response(status=429),
        _Response(text='<div class="result-container">你&amp;好</div>'),
        _Response(text='<div class="result-container">再次翻译</div>'),
    ]
    translator = GoogleFreeTranslator()
    profile = TranslationProfile(provider="google_free", timeout_seconds=8)

    first = translator.translate(_request(), profile)
    second = translator.translate(_request(), profile)

    assert first.translated == "你&好"
    assert second.translated == "再次翻译"
    assert [call[0] for call in _Client.calls] == [
        "https://translate.googleapis.com/translate_a/single",
        "https://translate.google.com/m",
        "https://translate.google.com/m",
    ]


def test_google_free_does_not_replace_a_user_configured_mirror() -> None:
    _Client.responses = [_Response(status=429)]

    with pytest.raises(TranslationError) as raised:
        GoogleFreeTranslator().translate(
            _request(),
            TranslationProfile(
                provider="google_free",
                base_url="https://mirror.example/translate",
                timeout_seconds=8,
            ),
        )

    assert raised.value.category == "quota"
    assert len(_Client.calls) == 1


def test_google_free_reports_when_both_free_paths_fail() -> None:
    _Client.responses = [
        _Response(status=429),
        _Response(text="<html>changed page</html>"),
    ]

    with pytest.raises(TranslationError) as raised:
        GoogleFreeTranslator().translate(
            _request(),
            TranslationProfile(provider="google_free", timeout_seconds=8),
        )

    assert raised.value.category == "response"
    assert "备用翻译未返回译文" in raised.value.user_message
    assert "原免费接口同时失败" in raised.value.user_message
