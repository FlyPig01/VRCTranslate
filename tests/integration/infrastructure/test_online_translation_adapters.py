from __future__ import annotations

import json

import httpx
import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.glossary import GlossaryInstruction
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.translation.deepl_translator import DeepLTranslator
from vrctranslate.infrastructure.translation.google_cloud_translator import GoogleCloudTranslator
from vrctranslate.infrastructure.translation.openai_compatible import (
    OpenAICompatibleTranslator,
)
from vrctranslate.infrastructure.translation.tencent_translator import (
    TencentTranslator,
)


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


def test_openai_compatible_sends_fixed_purpose_specific_messages(monkeypatch) -> None:
    from vrctranslate.infrastructure.translation import openai_compatible

    monkeypatch.setattr(openai_compatible.httpx, "Client", FakeClient)
    FakeClient.response = FakeResponse(
        {"choices": [{"message": {"content": "你现在在哪里？"}}]}
    )
    profile = TranslationProfile(
        provider="openai_compatible",
        base_url="https://example.invalid/v1",
        api_key="secret",
        model="chat-model",
        timeout_seconds=4,
    )

    result = OpenAICompatibleTranslator().translate(
        TranslationRequest(
            "1",
            "今どこ？",
            "ja",
            "zh-CN",
            "ocr",
            ("さっきのワールドにいるよ",),
            (GlossaryInstruction("アバター", "虚拟形象"),),
        ),
        profile,
    )

    endpoint, kwargs, timeout = FakeClient.last_call
    assert endpoint == "https://example.invalid/v1/chat/completions"
    assert kwargs["json"]["temperature"] == 0
    assert "OCR 翻译器" in kwargs["json"]["messages"][0]["content"]
    user_data = json.loads(kwargs["json"]["messages"][1]["content"])
    assert user_data["recent_context"] == ["さっきのワールドにいるよ"]
    assert user_data["glossary"] == [
        {"source": "アバター", "target": "虚拟形象"}
    ]
    assert timeout == 4
    assert result.translated == "你现在在哪里？"


def test_tencent_uses_tc3_signature_and_provider_credentials(monkeypatch) -> None:
    from vrctranslate.infrastructure.translation import tencent_translator

    monkeypatch.setattr(tencent_translator.httpx, "Client", FakeClient)
    FakeClient.response = FakeResponse({"Response": {"TargetText": "你好"}})
    profile = TranslationProfile(
        provider="tencent",
        base_url="tmt.tencentcloudapi.com",
        api_key="test-secret-id",
        model="test-secret-key",
        region="ap-beijing",
        timeout_seconds=8,
    )

    result = TencentTranslator().translate(
        TranslationRequest("1", "hello", "en", "zh-CN"),
        profile,
    )

    endpoint, kwargs, timeout = FakeClient.last_call
    headers = kwargs["headers"]
    payload = json.loads(kwargs["content"].decode("utf-8"))
    assert endpoint == "https://tmt.tencentcloudapi.com/"
    assert "Credential=test-secret-id/" in headers["Authorization"]
    assert headers["X-TC-Action"] == "TextTranslate"
    assert headers["X-TC-Region"] == "ap-beijing"
    assert payload == {
        "SourceText": "hello",
        "Source": "en",
        "Target": "zh",
        "ProjectId": 0,
    }
    assert timeout == 8
    assert result.translated == "你好"
