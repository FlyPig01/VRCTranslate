from __future__ import annotations

import base64
import hashlib
import hmac
import json
from collections.abc import Callable
from dataclasses import replace
from datetime import datetime, timezone
from urllib.parse import quote
from uuid import uuid4

import httpx
import websocket

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.application.speech_profiles import speech_service_descriptor
from vrctranslate.domain.speech import (
    AudioFrame,
    SpeechProfileValidationResult,
    SpeechRecognitionError,
    SpeechRecognitionRequest,
    SpeechRecognitionResult,
    SpeechServiceCapabilities,
    SpeechStreamConfig,
    SpeechStreamEvent,
)
from vrctranslate.infrastructure.speech.common import (
    ThreadedWebSocketSession,
    make_websocket_persistent,
    map_service_error,
    validation_pcm16,
)


DEFAULT_ALIYUN_NLS_URL = "wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1"
DEFAULT_ALIYUN_TOKEN_URL = "https://nls-meta.cn-shanghai.aliyuncs.com/"
DEFAULT_ALIYUN_REGION = "cn-shanghai"


class AliyunNlsRealtimeSession(ThreadedWebSocketSession):
    def __init__(self, *args, app_key: str, task_id: str, **kwargs) -> None:
        super().__init__(*args, **kwargs)
        self._app_key = app_key
        self._task_id = task_id

    def _send_audio(self, pcm16: bytes) -> None:
        self._websocket.send(pcm16, opcode=websocket.ABNF.OPCODE_BINARY)

    def _send_finish(self) -> None:
        self._websocket.send(
            json.dumps(
                _nls_command(
                    "StopTranscription",
                    self._app_key,
                    self._task_id,
                ),
                separators=(",", ":"),
            )
        )

    def _handle_message(self, message: object) -> bool:
        payload = _nls_json(message)
        header = payload.get("header")
        if not isinstance(header, dict):
            raise SpeechRecognitionError("protocol", "阿里云 NLS 响应缺少 header")
        name = str(header.get("name", ""))
        if name == "TaskFailed":
            self._report_error(
                _aliyun_nls_error(
                    str(header.get("status", "")),
                    str(header.get("status_text", "")),
                )
            )
            return True
        body = payload.get("payload")
        if not isinstance(body, dict):
            body = {}
        text = str(body.get("result", "")).strip()
        if name in {"TranscriptionResultChanged", "SentenceEnd"} and text:
            utterance_id = next(
                (
                    str(body[key])
                    for key in ("index", "sentence_id", "begin_time")
                    if body.get(key) is not None
                ),
                "current",
            )
            self._on_event(
                SpeechStreamEvent(
                    "final_transcript"
                    if name == "SentenceEnd"
                    else "partial_transcript",
                    text,
                    utterance_id,
                )
            )
        return name == "TranscriptionCompleted"


class AliyunNlsRealtimeSpeechRecognizer:
    provider = "aliyun_nls_realtime"

    def __init__(
        self,
        websocket_factory=websocket.create_connection,
        http_client_factory=lambda timeout: httpx.Client(timeout=timeout),
    ) -> None:
        self._websocket_factory = websocket_factory
        self._http_client_factory = http_client_factory

    def capabilities(
        self, _profile: SpeechRecognitionProfile
    ) -> SpeechServiceCapabilities:
        descriptor = speech_service_descriptor(self.provider)
        if descriptor is None:
            raise SpeechRecognitionError("configuration", "缺少阿里云 NLS 服务目录")
        # NLS binds the recognition model/language to the console project
        # identified by AppKey; SpeechTranscriber has no per-request automatic
        # language selection parameter.
        return replace(descriptor.capabilities, source_language_auto=False)

    def open_session(
        self,
        profile: SpeechRecognitionProfile,
        config: SpeechStreamConfig,
        on_event: Callable[[SpeechStreamEvent], None],
        on_error: Callable[[Exception], None],
    ) -> AliyunNlsRealtimeSession:
        del config
        self._validate_fields(profile)
        token = str(profile.options.get("access_token", "")).strip()
        if not token:
            token = create_aliyun_nls_token(profile, self._http_client_factory)
        app_key = str(profile.options.get("app_key", "")).strip()
        task_id = uuid4().hex
        try:
            connection = self._websocket_factory(
                profile.base_url.strip() or DEFAULT_ALIYUN_NLS_URL,
                header=[f"X-NLS-Token: {token}"],
                timeout=profile.timeout_seconds,
            )
            connection.send(
                json.dumps(
                    _nls_command(
                        "StartTranscription",
                        app_key,
                        task_id,
                        payload={
                            "format": "pcm",
                            "sample_rate": 16_000,
                            "enable_intermediate_result": True,
                            "enable_punctuation_prediction": True,
                            "enable_inverse_text_normalization": True,
                        },
                    ),
                    separators=(",", ":"),
                )
            )
            start_response = _nls_json(connection.recv())
            start_header = start_response.get("header")
            if not isinstance(start_header, dict):
                raise SpeechRecognitionError("protocol", "阿里云 NLS 启动响应无效")
            if str(start_header.get("name", "")) != "TranscriptionStarted":
                raise _aliyun_nls_error(
                    str(start_header.get("status", "")),
                    str(start_header.get("status_text", "")),
                )
            make_websocket_persistent(connection)
        except SpeechRecognitionError:
            try:
                connection.close()  # type: ignore[possibly-undefined]
            except Exception:
                pass
            raise
        except Exception as exc:
            raise map_service_error(
                exc, credential_name="阿里云 AppKey/AccessKey"
            ) from exc
        session = AliyunNlsRealtimeSession(
            connection,
            on_event,
            on_error,
            app_key=app_key,
            task_id=task_id,
        )
        session.start_receiver()
        return session

    def validate_profile(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechProfileValidationResult:
        errors: list[Exception] = []
        session = self.open_session(
            profile,
            SpeechStreamConfig(),
            lambda _event: None,
            errors.append,
        )
        responses_before_audio = session.response_count
        session.push_audio(AudioFrame(validation_pcm16(), 16_000))
        session.close()
        if errors or session.last_error:
            error = errors[0] if errors else session.last_error
            if isinstance(error, SpeechRecognitionError):
                raise error
            raise map_service_error(error, credential_name="阿里云 AppKey/AccessKey")
        if session.response_count <= responses_before_audio:
            raise SpeechRecognitionError("network", "阿里云 NLS 未返回实时音频响应")
        return SpeechProfileValidationResult(
            "verified", "阿里云 NLS 实时连接、鉴权和 PCM 传输验证通过"
        )

    def transcribe(
        self,
        request: SpeechRecognitionRequest,
        profile: SpeechRecognitionProfile,
    ) -> SpeechRecognitionResult:
        events: list[SpeechStreamEvent] = []
        errors: list[Exception] = []
        session = self.open_session(
            profile,
            SpeechStreamConfig(source_language=request.source_language),
            events.append,
            errors.append,
        )
        session.push_audio(AudioFrame(request.pcm16, request.sample_rate))
        session.close()
        if errors:
            error = errors[0]
            if isinstance(error, SpeechRecognitionError):
                raise error
            raise map_service_error(error)
        text = "".join(
            event.text for event in events if event.kind == "final_transcript"
        ).strip()
        if not text:
            raise SpeechRecognitionError("response", "阿里云 NLS 语音识别返回了空文本")
        return SpeechRecognitionResult(request.request_id, text)

    @staticmethod
    def _validate_fields(profile: SpeechRecognitionProfile) -> None:
        if not str(profile.options.get("app_key", "")).strip():
            raise SpeechRecognitionError("configuration", "未填写阿里云智能语音 AppKey")
        if str(profile.options.get("access_token", "")).strip():
            return
        if not str(profile.options.get("access_key_id", "")).strip():
            raise SpeechRecognitionError("configuration", "未填写阿里云 AccessKey ID")
        if not profile.api_key.strip():
            raise SpeechRecognitionError("configuration", "未填写阿里云 AccessKey Secret")


def create_aliyun_nls_token(
    profile: SpeechRecognitionProfile,
    http_client_factory=lambda timeout: httpx.Client(timeout=timeout),
    *,
    timestamp: datetime | None = None,
    nonce: str | None = None,
) -> str:
    """Create a short-lived NLS token using Alibaba Cloud RPC signing."""

    current = timestamp or datetime.now(timezone.utc)
    access_key_id = str(profile.options.get("access_key_id", "")).strip()
    region_id = str(
        profile.options.get("region_id", DEFAULT_ALIYUN_REGION)
    ).strip() or DEFAULT_ALIYUN_REGION
    params = {
        "AccessKeyId": access_key_id,
        "Action": "CreateToken",
        "Format": "JSON",
        "RegionId": region_id,
        "SignatureMethod": "HMAC-SHA1",
        "SignatureNonce": nonce or str(uuid4()),
        # aliyun-python-sdk-core includes this empty field in both the
        # canonical string and final RPC request.
        "SignatureType": "",
        "SignatureVersion": "1.0",
        "Timestamp": current.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "Version": "2019-02-28",
    }
    canonical = "&".join(
        f"{_rpc_quote(key)}={_rpc_quote(value)}"
        for key, value in sorted(params.items())
    )
    string_to_sign = f"POST&%2F&{_rpc_quote(canonical)}"
    signature = base64.b64encode(
        hmac.new(
            f"{profile.api_key}&".encode("utf-8"),
            string_to_sign.encode("utf-8"),
            hashlib.sha1,
        ).digest()
    ).decode("ascii")
    request_params = {**params, "Signature": signature}
    endpoint = str(profile.options.get("token_endpoint", "")).strip()
    endpoint = endpoint or DEFAULT_ALIYUN_TOKEN_URL
    try:
        with http_client_factory(profile.timeout_seconds) as client:
            response = client.post(endpoint, params=request_params)
            payload = response.json()
            if isinstance(payload, dict) and payload.get("Code"):
                raise _aliyun_token_error(
                    str(payload.get("Code", "")),
                    str(payload.get("Message", "")),
                )
            response.raise_for_status()
    except SpeechRecognitionError:
        raise
    except Exception as exc:
        raise map_service_error(
            exc, credential_name="阿里云 AccessKey ID/Secret"
        ) from exc
    token = payload.get("Token") if isinstance(payload, dict) else None
    token_id = token.get("Id") if isinstance(token, dict) else None
    if not str(token_id or "").strip():
        raise SpeechRecognitionError(
            "authentication", "阿里云未返回有效的 NLS 访问 Token"
        )
    return str(token_id)


def _rpc_quote(value: object) -> str:
    return quote(str(value), safe="~")


def _nls_command(
    name: str,
    app_key: str,
    task_id: str,
    *,
    payload: dict[str, object] | None = None,
) -> dict[str, object]:
    command: dict[str, object] = {
        "header": {
            "message_id": uuid4().hex,
            "task_id": task_id,
            "namespace": "SpeechTranscriber",
            "name": name,
            "appkey": app_key,
        },
        "context": {
            "sdk": {
                "name": "vrctranslate",
                "version": "1",
                "language": "python",
            }
        },
    }
    if payload is not None:
        command["payload"] = payload
    return command


def _nls_json(message: object) -> dict[str, object]:
    try:
        if isinstance(message, bytes):
            message = message.decode("utf-8")
        payload = json.loads(str(message))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise SpeechRecognitionError("protocol", "阿里云 NLS 返回了无效的 JSON") from exc
    if not isinstance(payload, dict):
        raise SpeechRecognitionError("protocol", "阿里云 NLS 返回了无效的响应结构")
    return payload


def _aliyun_nls_error(status: str, status_text: str) -> SpeechRecognitionError:
    folded = f"{status} {status_text}".casefold()
    if any(
        value in folded
        for value in ("token", "appkey", "unauthorized", "forbidden", "400000")
    ):
        return SpeechRecognitionError(
            "authentication", "阿里云 NLS 认证失败，请检查 AppKey 和 AccessKey"
        )
    if "quota" in folded or "limit" in folded:
        return SpeechRecognitionError("quota", "阿里云 NLS 额度不足或并发超限")
    return SpeechRecognitionError("service", "阿里云 NLS 实时语音识别启动失败")


def _aliyun_token_error(code: str, message: str) -> SpeechRecognitionError:
    folded = f"{code} {message}".casefold()
    if any(
        value in folded
        for value in (
            "invalidaccesskeyid",
            "signaturedoesnotmatch",
            "unauthorized",
            "forbidden",
            "access denied",
        )
    ):
        return SpeechRecognitionError(
            "authentication",
            "阿里云 Token 鉴权失败，请检查 AccessKey ID、AccessKey Secret 和 RAM 权限",
        )
    if "invalidregion" in folded:
        return SpeechRecognitionError(
            "configuration", "阿里云 NLS 地域无效，当前服务应使用 cn-shanghai"
        )
    if any(value in folded for value in ("throttl", "quota", "limit")):
        return SpeechRecognitionError(
            "quota", "阿里云 Token 请求过多或账号额度受限"
        )
    return SpeechRecognitionError(
        "service", f"阿里云 NLS Token 创建失败（错误码 {code or 'unknown'}）"
    )
