from __future__ import annotations

from types import SimpleNamespace

import pytest
from Tea.exceptions import TeaException

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.translation.aliyun_translator import (
    AliyunTranslator,
)


class _Client:
    config = None
    general_request = None
    professional_request = None
    runtime = None
    error: Exception | None = None

    def __init__(self, config) -> None:
        type(self).config = config

    @classmethod
    def reset(cls) -> None:
        cls.config = None
        cls.general_request = None
        cls.professional_request = None
        cls.runtime = None
        cls.error = None

    def translate_general_with_options(self, request, runtime):
        type(self).general_request = request
        type(self).runtime = runtime
        if type(self).error is not None:
            raise type(self).error
        return _response("你好", "en")

    def translate_with_options(self, request, runtime):
        type(self).professional_request = request
        type(self).runtime = runtime
        if type(self).error is not None:
            raise type(self).error
        return _response("专业译文", "ja")


def _response(translated: str, detected: str):
    return SimpleNamespace(
        body=SimpleNamespace(
            code="200",
            message="",
            data=SimpleNamespace(
                translated=translated,
                detected_language=detected,
            ),
        )
    )


@pytest.fixture(autouse=True)
def _fake_sdk(monkeypatch):
    from vrctranslate.infrastructure.translation import aliyun_translator

    _Client.reset()
    monkeypatch.setattr(aliyun_translator, "AlimtClient", _Client)


def _profile(**changes) -> TranslationProfile:
    values = {
        "provider": "aliyun",
        "api_key": "test-access-key-id",
        "model": "test-access-key-secret",
        "region": "ap-southeast-1",
        "timeout_seconds": 8,
        "options": {"aliyun_api": "general"},
    }
    values.update(changes)
    return TranslationProfile(**values)


def test_aliyun_general_uses_region_endpoint_credentials_and_auto_detection() -> None:
    result = AliyunTranslator().translate(
        TranslationRequest("ali-1", "hello", "auto", "zh-CN", "ocr"),
        _profile(base_url="https://mt.ap-southeast-1.aliyuncs.com"),
    )

    assert result.translated == "你好"
    assert result.source_language == "en"
    assert _Client.config.access_key_id == "test-access-key-id"
    assert _Client.config.access_key_secret == "test-access-key-secret"
    assert _Client.config.region_id == "ap-southeast-1"
    assert _Client.config.endpoint == "mt.ap-southeast-1.aliyuncs.com"
    assert _Client.general_request.source_language == "auto"
    assert _Client.general_request.target_language == "zh"
    assert _Client.general_request.source_text == "hello"
    assert _Client.general_request.scene == "general"
    assert _Client.runtime.autoretry is False
    assert _Client.runtime.read_timeout == 8000


def test_aliyun_professional_uses_translate_api_and_sdk_endpoint_resolution() -> None:
    result = AliyunTranslator().translate(
        TranslationRequest("ali-2", "こんにちは", "ja", "zh-CN", "self"),
        _profile(
            base_url="",
            region="cn-beijing",
            options={"aliyun_api": "professional"},
        ),
    )

    assert result.translated == "专业译文"
    assert _Client.config.region_id == "cn-beijing"
    assert _Client.config.endpoint is None
    assert _Client.general_request is None
    assert _Client.professional_request.source_language == "ja"
    assert _Client.professional_request.target_language == "zh"


def test_aliyun_maps_sdk_authentication_error_without_exposing_credentials() -> None:
    _Client.error = TeaException(
        {
            "code": "InvalidAccessKeyId.NotFound",
            "message": "specified access key is not found",
        }
    )

    with pytest.raises(TranslationError) as raised:
        AliyunTranslator().translate(
            TranslationRequest("ali-3", "hello", "en", "zh-CN"),
            _profile(),
        )

    assert raised.value.category == "authentication"
    assert "test-access-key" not in raised.value.user_message
    assert "AccessKey" in raised.value.user_message


def test_aliyun_rejects_endpoint_paths_before_calling_sdk() -> None:
    with pytest.raises(TranslationError) as raised:
        AliyunTranslator().translate(
            TranslationRequest("ali-4", "hello", "en", "zh-CN"),
            _profile(base_url="https://mt.aliyuncs.com/v1"),
        )

    assert raised.value.category == "configuration"
    assert _Client.config is None


def test_aliyun_requires_explicit_resource_region() -> None:
    with pytest.raises(TranslationError, match="资源所在地域"):
        AliyunTranslator().translate(
            TranslationRequest("ali-5", "hello", "en", "zh-CN"),
            _profile(region=""),
        )

    assert _Client.config is None
