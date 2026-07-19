from __future__ import annotations

import base64
import hashlib
import hmac
import json
from datetime import datetime, timezone
from urllib.parse import parse_qs, urlsplit

import pytest

from vrctranslate.application.dto import AppSettings, SpeechRecognitionProfile
from vrctranslate.application.speech_profiles import (
    creatable_speech_services,
    profile_validation_state,
    set_profile_validation,
)
from vrctranslate.domain.speech import SpeechRecognitionError, SpeechStreamConfig
from vrctranslate.infrastructure.speech import (
    AliyunNlsRealtimeSpeechRecognizer,
    TencentRealtimeSpeechRecognizer,
)
from vrctranslate.infrastructure.speech.aliyun_nls_realtime import (
    DEFAULT_ALIYUN_NLS_URL,
    _aliyun_token_error,
    create_aliyun_nls_token,
)
from vrctranslate.infrastructure.speech.tencent_realtime import (
    TENCENT_ASR_HOST,
    _tencent_error,
    build_tencent_signed_url,
)


class _FakeWebSocket:
    def __init__(self, responses: list[object]) -> None:
        self.responses = list(responses)
        self.sent: list[tuple[object, int | None]] = []
        self.closed = False

    def send(self, payload, opcode=None):
        self.sent.append((payload, opcode))

    def recv(self):
        return self.responses.pop(0) if self.responses else b""

    def close(self):
        self.closed = True


def test_service_catalog_only_creates_the_two_asr_providers() -> None:
    descriptors = creatable_speech_services()

    assert {item.provider for item in descriptors} == {
        "tencent_realtime",
        "aliyun_nls_realtime",
    }
    assert all(item.capabilities.final_transcript for item in descriptors)


def test_tencent_signature_is_sorted_and_fully_url_encoded() -> None:
    profile = SpeechRecognitionProfile(
        provider="tencent_realtime",
        api_key="secret-key",
        model="16k_ja",
        options={"app_id": "123456", "secret_id": "secret-id"},
    )

    url = build_tencent_signed_url(
        profile,
        timestamp=1_700_000_000,
        voice_id="voice-id",
    )
    parsed = urlsplit(url)
    params = parse_qs(parsed.query)

    assert parsed.path == "/asr/v2/123456"
    assert params["engine_model_type"] == ["16k_ja"]
    assert params["secretid"] == ["secret-id"]
    assert params["voice_id"] == ["voice-id"]
    assert params["signature"][0]


def test_tencent_signature_uses_raw_values_before_request_url_encoding() -> None:
    profile = SpeechRecognitionProfile(
        provider="tencent_realtime",
        api_key="secret-key",
        model="custom engine/ja",
        options={"app_id": "123456", "secret_id": "secret+id/value"},
    )

    url = build_tencent_signed_url(
        profile,
        timestamp=1_700_000_000,
        voice_id="voice id/value",
    )
    params = parse_qs(urlsplit(url).query)
    sign_query = "&".join(
        (
            "engine_model_type=custom engine/ja",
            "expired=1700086400",
            "needvad=1",
            "nonce=1700000000",
            "secretid=secret+id/value",
            "timestamp=1700000000",
            "voice_format=1",
            "voice_id=voice id/value",
        )
    )
    expected = base64.b64encode(
        hmac.new(
            b"secret-key",
            f"{TENCENT_ASR_HOST}/asr/v2/123456?{sign_query}".encode(),
            hashlib.sha1,
        ).digest()
    ).decode()

    assert params["engine_model_type"] == ["custom engine/ja"]
    assert params["secretid"] == ["secret+id/value"]
    assert params["voice_id"] == ["voice id/value"]
    assert params["signature"] == [expected]


@pytest.mark.parametrize(
    ("code", "category", "message_part"),
    (
        (4001, "configuration", "请求参数"),
        (4002, "authentication", "认证失败"),
        (4003, "configuration", "尚未开通"),
        (4004, "quota", "资源包"),
        (4005, "quota", "欠费"),
        (4006, "quota", "并发数"),
        (4007, "configuration", "音频解码"),
        (4008, "network", "15 秒"),
        (4009, "network", "连接已断开"),
    ),
)
def test_tencent_official_error_codes_are_not_all_reported_as_authentication(
    code: int, category: str, message_part: str
) -> None:
    error = _tencent_error(code, "")

    assert error.category == category
    assert message_part in error.user_message


def test_tencent_accepts_official_multilingual_and_custom_engine_parameters() -> None:
    for engine in ("16k_multi_lang", "account_custom_engine"):
        profile = SpeechRecognitionProfile(
            provider="tencent_realtime",
            api_key="secret-key",
            model=engine,
            options={"app_id": "123456", "secret_id": "secret-id"},
        )

        TencentRealtimeSpeechRecognizer._validate_fields(profile)
        params = parse_qs(
            urlsplit(
                build_tencent_signed_url(
                    profile,
                    timestamp=1_700_000_000,
                    voice_id="voice-id",
                )
            ).query
        )

        assert params["engine_model_type"] == [engine]


def test_provider_capabilities_report_actual_automatic_language_support() -> None:
    tencent = TencentRealtimeSpeechRecognizer()
    single_language = SpeechRecognitionProfile(
        provider="tencent_realtime",
        model="16k_ja",
    )
    multilingual = SpeechRecognitionProfile(
        provider="tencent_realtime",
        model="16k_multi_lang",
    )
    aliyun = AliyunNlsRealtimeSpeechRecognizer()

    assert not tencent.capabilities(single_language).source_language_auto
    assert tencent.capabilities(multilingual).source_language_auto
    assert not aliyun.capabilities(
        SpeechRecognitionProfile(provider="aliyun_nls_realtime")
    ).source_language_auto


def test_tencent_stream_maps_partial_and_final_results() -> None:
    handshake = json.dumps({"code": 0, "message": "success"})
    partial = json.dumps(
        {"code": 0, "result": {"slice_type": 1, "index": 0, "voice_text_str": "こん"}}
    )
    final = json.dumps(
        {"code": 0, "result": {"slice_type": 2, "index": 0, "voice_text_str": "こんにちは"}}
    )
    socket = _FakeWebSocket([handshake, partial, final, json.dumps({"code": 0, "final": 1})])
    recognizer = TencentRealtimeSpeechRecognizer(lambda *args, **kwargs: socket)
    profile = SpeechRecognitionProfile(
        provider="tencent_realtime",
        api_key="secret-key",
        model="16k_ja",
        options={"app_id": "123", "secret_id": "id"},
    )
    events = []
    session = recognizer.open_session(
        profile,
        SpeechStreamConfig(),
        events.append,
        lambda error: pytest.fail(str(error)),
    )
    session.close()

    assert [event.kind for event in events] == ["partial_transcript", "final_transcript"]
    assert events[-1].text == "こんにちは"


class _TokenResponse:
    def raise_for_status(self) -> None:
        return

    def json(self):
        return {"Token": {"Id": "short-lived-token"}}


class _TokenClient:
    def __init__(self) -> None:
        self.params = None

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def post(self, _endpoint, *, params):
        self.params = params
        return _TokenResponse()


def test_aliyun_access_keys_are_used_only_to_create_a_short_lived_nls_token() -> None:
    client = _TokenClient()
    profile = SpeechRecognitionProfile(
        provider="aliyun_nls_realtime",
        api_key="access-secret",
        model="nls-realtime",
        options={"app_key": "app-key", "access_key_id": "access-id"},
    )

    token = create_aliyun_nls_token(
        profile,
        lambda _timeout: client,
        timestamp=datetime(2026, 1, 1, tzinfo=timezone.utc),
        nonce="fixed-nonce",
    )

    assert token == "short-lived-token"
    assert client.params["Action"] == "CreateToken"
    assert client.params["AccessKeyId"] == "access-id"
    assert client.params["RegionId"] == "cn-shanghai"
    assert client.params["SignatureType"] == ""
    # Independent value produced by aliyun-python-sdk-core 2.16.0 for this
    # fixed request. This guards all canonicalization fields, not just the
    # presence of some signature.
    assert client.params["Signature"] == "04NC29hPEntL+fep9M6G2qY5mcY="
    assert "access-secret" not in client.params.values()


def test_aliyun_nls_stream_maps_intermediate_and_sentence_end() -> None:
    started = json.dumps({"header": {"name": "TranscriptionStarted"}})
    partial = json.dumps(
        {"header": {"name": "TranscriptionResultChanged"}, "payload": {"result": "你好", "index": 0}}
    )
    final = json.dumps(
        {"header": {"name": "SentenceEnd"}, "payload": {"result": "你好世界", "index": 0}}
    )
    completed = json.dumps({"header": {"name": "TranscriptionCompleted"}})
    socket = _FakeWebSocket([started, partial, final, completed])
    recognizer = AliyunNlsRealtimeSpeechRecognizer(
        lambda *args, **kwargs: socket,
    )
    profile = SpeechRecognitionProfile(
        provider="aliyun_nls_realtime",
        api_key="unused-when-token-present",
        model="nls-realtime",
        options={"app_key": "app-key", "access_token": "token"},
    )
    events = []
    session = recognizer.open_session(
        profile,
        SpeechStreamConfig(),
        events.append,
        lambda error: pytest.fail(str(error)),
    )
    session.close()

    assert [event.kind for event in events] == ["partial_transcript", "final_transcript"]
    assert events[-1].text == "你好世界"
    assert events[-1].utterance_id == "0"


def test_aliyun_uses_the_current_official_nls_websocket_endpoint() -> None:
    started = json.dumps({"header": {"name": "TranscriptionStarted"}})
    completed = json.dumps({"header": {"name": "TranscriptionCompleted"}})
    socket = _FakeWebSocket([started, completed])
    captured: dict[str, object] = {}

    def factory(url, **kwargs):
        captured["url"] = url
        captured["header"] = kwargs.get("header")
        return socket

    recognizer = AliyunNlsRealtimeSpeechRecognizer(factory)
    profile = SpeechRecognitionProfile(
        provider="aliyun_nls_realtime",
        api_key="unused-when-token-present",
        model="nls-realtime",
        options={"app_key": "app-key", "access_token": "short-lived-token"},
    )

    session = recognizer.open_session(
        profile,
        SpeechStreamConfig(),
        lambda _event: None,
        lambda error: pytest.fail(str(error)),
    )
    session.close()

    assert captured["url"] == DEFAULT_ALIYUN_NLS_URL
    assert captured["header"] == ["X-NLS-Token: short-lived-token"]


def test_aliyun_token_authentication_error_is_actionable() -> None:
    error = _aliyun_token_error(
        "SignatureDoesNotMatch", "specified signature is not matched"
    )

    assert error.category == "authentication"
    assert "AccessKey" in error.user_message


def test_validation_is_invalidated_when_provider_secret_changes() -> None:
    profile = SpeechRecognitionProfile(
        provider="tencent_realtime",
        api_key="first-key",
        model="16k_zh",
        options={"app_id": "123", "secret_id": "id"},
    )
    set_profile_validation(profile, "verified", "ok")
    assert profile_validation_state(profile) == "verified"

    profile.options["secret_id"] = "changed-id"

    assert profile_validation_state(profile) == "pending"


def test_legacy_profile_is_deleted_by_settings_normalization() -> None:
    settings = AppSettings()
    settings.voice.asr_profiles = [
        SpeechRecognitionProfile(
            provider="dashscope_realtime",
            api_key="obsolete-secret",
            model="paraformer-realtime-v2",
        )
    ]

    settings.voice.ensure_profiles()

    assert settings.voice.asr_profiles == []
    assert settings.voice.asr_profile_id == ""


def test_provider_specific_missing_fields_have_actionable_errors() -> None:
    recognizer = TencentRealtimeSpeechRecognizer()
    profile = SpeechRecognitionProfile(
        provider="tencent_realtime",
        api_key="",
        model="16k_zh",
    )

    with pytest.raises(SpeechRecognitionError) as captured:
        recognizer.open_session(profile, SpeechStreamConfig(), lambda _event: None, lambda _error: None)

    assert captured.value.category == "configuration"
    assert "AppID" in captured.value.user_message
