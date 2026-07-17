from __future__ import annotations

import httpx
import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.translation.deepl_translator import DeepLTranslator
from vrctranslate.infrastructure.translation.google_cloud_translator import GoogleCloudTranslator


class FakeResponse:
    def __init__(self, data, status: int = 200) -> None:
        self._data = data
        self.status_code = status

    def json(self):
        return self._data

    def raise_for_status(self):
        if self.status_code >= 400:
            request = httpx.Request("POST", "https://example.invalid")
            response = httpx.Response(self.status_code, request=request)
            raise httpx.HTTPStatusError("failed", request=request, response=response)


class FakeClient:
    response = FakeResponse({})
    last_call = None

    def __init__(self, timeout):
        self.timeout = timeout

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def post(self, endpoint, **kwargs):
        type(self).last_call = (endpoint, kwargs, self.timeout)
        return type(self).response


def test_deepl_uses_official_payload_and_maps_language(monkeypatch) -> None:
    from vrctranslate.infrastructure.translation import deepl_translator

    FakeClient.response = FakeResponse({"translations": [{"text": "你好"}]})
    monkeypatch.setattr(deepl_translator.httpx, "Client", FakeClient)
    profile = TranslationProfile(provider="deepl", api_key="key:fx", timeout_seconds=3)
    result = DeepLTranslator().translate(
        TranslationRequest("1", "hello", "en", "zh-CN"), profile
    )
    endpoint, kwargs, timeout = FakeClient.last_call
    assert endpoint == "https://api-free.deepl.com/v2/translate"
    assert kwargs["headers"]["Authorization"] == "DeepL-Auth-Key key:fx"
    assert kwargs["data"]["target_lang"] == "ZH-HANS"
    assert timeout == 3
    assert result.translated == "你好"


def test_deepl_batches_multiple_ocr_lines_in_one_request(monkeypatch) -> None:
    from vrctranslate.infrastructure.translation import deepl_translator

    FakeClient.response = FakeResponse(
        {"translations": [{"text": "一"}, {"text": "二"}]}
    )
    monkeypatch.setattr(deepl_translator.httpx, "Client", FakeClient)
    results = DeepLTranslator().translate_batch(
        [
            TranslationRequest("1", "one", "en", "zh-CN", "ocr"),
            TranslationRequest("2", "two", "en", "zh-CN", "ocr"),
        ],
        TranslationProfile(provider="deepl", api_key="key:fx"),
    )
    assert [result.translated for result in results] == ["一", "二"]
    assert FakeClient.last_call[1]["data"]["text"] == ["one", "two"]


def test_google_decodes_response_and_maps_quota_error(monkeypatch) -> None:
    from vrctranslate.infrastructure.translation import google_cloud_translator

    monkeypatch.setattr(google_cloud_translator.httpx, "Client", FakeClient)
    profile = TranslationProfile(provider="google_cloud", api_key="key")
    FakeClient.response = FakeResponse(
        {"data": {"translations": [{"translatedText": "Tom &amp; Jerry"}]}}
    )
    result = GoogleCloudTranslator().translate(
        TranslationRequest("1", "text", "auto", "en"), profile
    )
    assert result.translated == "Tom & Jerry"
    assert FakeClient.last_call[1]["params"] == {"key": "key"}
    assert FakeClient.last_call[1]["json"]["q"] == ["text"]

    FakeClient.response = FakeResponse({}, status=429)
    with pytest.raises(TranslationError) as raised:
        GoogleCloudTranslator().translate(
            TranslationRequest("2", "text", "auto", "en"), profile
        )
    assert raised.value.category == "quota"
