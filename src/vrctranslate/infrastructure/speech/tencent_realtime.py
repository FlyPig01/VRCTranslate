from __future__ import annotations

import base64
import hashlib
import hmac
import json
import time
from collections.abc import Callable
from dataclasses import replace
from urllib.parse import quote, urlencode
from uuid import uuid4

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


TENCENT_ASR_HOST = "asr.cloud.tencent.com"


class TencentRealtimeSession(ThreadedWebSocketSession):
    def _send_audio(self, pcm16: bytes) -> None:
        self._websocket.send(pcm16, opcode=websocket.ABNF.OPCODE_BINARY)

    def _send_finish(self) -> None:
        self._websocket.send(json.dumps({"type": "end"}))

    def _handle_message(self, message: object) -> bool:
        payload = _json_message(message)
        code = int(payload.get("code", 0) or 0)
        if code:
            self._report_error(_tencent_error(code, str(payload.get("message", ""))))
            return True
        result = payload.get("result")
        if isinstance(result, dict):
            text = str(result.get("voice_text_str", "")).strip()
            slice_type = int(result.get("slice_type", -1) or 0)
            if text and slice_type in {1, 2}:
                self._on_event(
                    SpeechStreamEvent(
                        "final_transcript" if slice_type == 2 else "partial_transcript",
                        text,
                        str(result.get("index", "current")),
                    )
                )
        return int(payload.get("final", 0) or 0) == 1


class TencentRealtimeSpeechRecognizer:
    provider = "tencent_realtime"

    def __init__(self, websocket_factory=websocket.create_connection) -> None:
        self._websocket_factory = websocket_factory

    def capabilities(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechServiceCapabilities:
        descriptor = speech_service_descriptor(self.provider)
        if descriptor is None:
            raise SpeechRecognitionError("configuration", "缺少腾讯云服务目录")
        return replace(
            descriptor.capabilities,
            source_language_auto=profile.model
            in {"16k_zh_en", "16k_multi_lang"},
        )

    def open_session(
        self,
        profile: SpeechRecognitionProfile,
        config: SpeechStreamConfig,
        on_event: Callable[[SpeechStreamEvent], None],
        on_error: Callable[[Exception], None],
    ) -> TencentRealtimeSession:
        del config
        self._validate_fields(profile)
        try:
            url = build_tencent_signed_url(profile)
            connection = self._websocket_factory(
                url,
                timeout=profile.timeout_seconds,
            )
            handshake = _json_message(connection.recv())
            code = int(handshake.get("code", 0) or 0)
            if code:
                connection.close()
                raise _tencent_error(code, str(handshake.get("message", "")))
            make_websocket_persistent(connection)
        except SpeechRecognitionError:
            raise
        except Exception as exc:
            raise map_service_error(exc, credential_name="腾讯云 SecretId/SecretKey") from exc
        session = TencentRealtimeSession(connection, on_event, on_error)
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
            raise map_service_error(error, credential_name="腾讯云 SecretId/SecretKey")
        if session.response_count <= responses_before_audio:
            raise SpeechRecognitionError("network", "腾讯云未返回实时音频响应")
        return SpeechProfileValidationResult(
            "verified", "腾讯云实时连接、签名鉴权和 PCM 传输验证通过"
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
            raise SpeechRecognitionError("response", "腾讯云语音识别返回了空文本")
        return SpeechRecognitionResult(request.request_id, text)

    @staticmethod
    def _validate_fields(profile: SpeechRecognitionProfile) -> None:
        if not str(profile.options.get("app_id", "")).strip():
            raise SpeechRecognitionError("configuration", "未填写腾讯云 AppID")
        if not str(profile.options.get("secret_id", "")).strip():
            raise SpeechRecognitionError("configuration", "未填写腾讯云 SecretId")
        if not profile.api_key.strip():
            raise SpeechRecognitionError("configuration", "未填写腾讯云 SecretKey")
        if not profile.model.strip():
            raise SpeechRecognitionError("configuration", "未选择腾讯云识别引擎")


def build_tencent_signed_url(
    profile: SpeechRecognitionProfile,
    *,
    timestamp: int | None = None,
    voice_id: str | None = None,
) -> str:
    """Build the exact v2 WebSocket HMAC-SHA1 request URL."""

    now = int(time.time()) if timestamp is None else int(timestamp)
    app_id = str(profile.options.get("app_id", "")).strip()
    secret_id = str(profile.options.get("secret_id", "")).strip()
    identifier = voice_id or str(uuid4())
    params: dict[str, object] = {
        "engine_model_type": profile.model.strip(),
        "expired": now + 86_400,
        "needvad": 1,
        "nonce": now,
        "secretid": secret_id,
        "timestamp": now,
        "voice_format": 1,
        "voice_id": identifier,
    }
    ordered_params = sorted(params.items())
    # Tencent signs the original parameter values. URL escaping belongs only to
    # the final request URL; using the escaped query as the signature source
    # breaks custom engine/voice identifiers containing reserved characters.
    sign_query = "&".join(f"{key}={value}" for key, value in ordered_params)
    request_query = urlencode(ordered_params, quote_via=quote, safe="")
    request_path = f"{TENCENT_ASR_HOST}/asr/v2/{app_id}"
    sign_source = f"{request_path}?{sign_query}"
    signature = base64.b64encode(
        hmac.new(
            profile.api_key.encode("utf-8"),
            sign_source.encode("utf-8"),
            hashlib.sha1,
        ).digest()
    ).decode("ascii")
    return (
        f"wss://{request_path}?{request_query}"
        f"&signature={quote(signature, safe='')}"
    )


def _json_message(message: object) -> dict[str, object]:
    try:
        if isinstance(message, bytes):
            message = message.decode("utf-8")
        payload = json.loads(str(message))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise SpeechRecognitionError("protocol", "腾讯云返回了无效的 JSON 响应") from exc
    if not isinstance(payload, dict):
        raise SpeechRecognitionError("protocol", "腾讯云返回了无效的响应结构")
    return payload


def _tencent_error(code: int, message: str) -> SpeechRecognitionError:
    folded = message.casefold()
    if code == 4000:
        return SpeechRecognitionError(
            "service", "腾讯云拒绝了过快的音频发送，请稍后重试"
        )
    if code == 4001:
        if "engine" in folded or "引擎" in message:
            return SpeechRecognitionError(
                "configuration",
                "腾讯云识别引擎参数无效或当前账号未开通该引擎",
            )
        return SpeechRecognitionError(
            "configuration",
            "腾讯云请求参数不合法，请检查 AppID 和识别引擎",
        )
    if code == 4002 or any(
        value in folded for value in ("signature", "secret", "鉴权", "认证")
    ):
        return SpeechRecognitionError(
            "authentication", "腾讯云认证失败，请检查 AppID、SecretId 和 SecretKey"
        )
    if code == 4003:
        return SpeechRecognitionError(
            "configuration", "腾讯云 AppID 尚未开通实时语音识别服务"
        )
    if code == 4004:
        return SpeechRecognitionError(
            "quota", "腾讯云语音识别资源包已耗尽，请购买资源包或开通后付费"
        )
    if code == 4005:
        return SpeechRecognitionError(
            "quota", "腾讯云账户欠费，实时语音识别服务已停止"
        )
    if code == 4006:
        return SpeechRecognitionError(
            "quota", "腾讯云实时语音识别并发数已达到上限"
        )
    if code == 4007:
        return SpeechRecognitionError(
            "configuration", "腾讯云音频解码失败，请检查 PCM 格式和采样率"
        )
    if code == 4008:
        return SpeechRecognitionError(
            "network", "腾讯云连接因超过 15 秒未收到音频而关闭"
        )
    if code == 4009:
        return SpeechRecognitionError("network", "腾讯云实时语音识别连接已断开")
    if "余额" in message or "额度" in message or "quota" in folded:
        return SpeechRecognitionError("quota", "腾讯云语音识别额度不足")
    if "engine" in folded or "引擎" in message:
        return SpeechRecognitionError("configuration", "腾讯云识别引擎无效或未开通")
    return SpeechRecognitionError("service", f"腾讯云语音识别失败（错误码 {code}）")
