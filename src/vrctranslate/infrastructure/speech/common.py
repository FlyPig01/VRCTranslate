from __future__ import annotations

from collections.abc import Callable
from threading import Event, Lock, Thread
from typing import Protocol

import numpy as np

from vrctranslate.domain.speech import (
    AudioFrame,
    SpeechRecognitionError,
    SpeechStreamEvent,
)


class WebSocketConnection(Protocol):
    def send(self, payload: object, opcode: int | None = None) -> object: ...

    def recv(self) -> object: ...

    def close(self) -> object: ...


def make_websocket_persistent(connection: object) -> None:
    """Keep the connect timeout from becoming an idle-session read timeout."""

    setter = getattr(connection, "settimeout", None)
    if callable(setter):
        setter(None)


class ThreadedWebSocketSession:
    """Small shared lifecycle wrapper; provider subclasses own the protocol."""

    def __init__(
        self,
        websocket: WebSocketConnection,
        on_event: Callable[[SpeechStreamEvent], None],
        on_error: Callable[[Exception], None],
    ) -> None:
        self._websocket = websocket
        self._on_event = on_event
        self._on_error = on_error
        self._lock = Lock()
        self._closed = False
        self._finished = Event()
        self._received = Event()
        self._response_count = 0
        self._last_error: SpeechRecognitionError | None = None
        self._receiver = Thread(target=self._receive_loop, daemon=True)

    @property
    def last_error(self) -> SpeechRecognitionError | None:
        return self._last_error

    @property
    def received_response(self) -> bool:
        return self._received.is_set()

    @property
    def response_count(self) -> int:
        return self._response_count

    def start_receiver(self) -> None:
        self._receiver.start()

    def push_audio(self, frame: AudioFrame) -> None:
        if not frame.pcm16:
            return
        payload = resample_pcm16(frame.pcm16, frame.sample_rate, 16_000)
        with self._lock:
            if self._closed:
                return
            try:
                self._send_audio(payload)
            except SpeechRecognitionError:
                raise
            except Exception as exc:
                raise map_service_error(exc) from exc

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            try:
                self._send_finish()
            except Exception as exc:
                self._report_error(
                    exc if isinstance(exc, SpeechRecognitionError) else map_service_error(exc)
                )
        self._finished.wait(4.0)
        self._close_socket()

    def cancel(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
        self._close_socket()

    def _receive_loop(self) -> None:
        try:
            while True:
                message = self._websocket.recv()
                if message in (None, b"", ""):
                    break
                self._received.set()
                self._response_count += 1
                if self._handle_message(message):
                    break
        except Exception as exc:
            with self._lock:
                closed = self._closed
            if not closed:
                self._report_error(
                    exc if isinstance(exc, SpeechRecognitionError) else map_service_error(exc)
                )
        finally:
            self._finished.set()

    def _report_error(self, error: SpeechRecognitionError) -> None:
        if self._last_error is not None:
            return
        self._last_error = error
        self._on_error(error)

    def _close_socket(self) -> None:
        try:
            self._websocket.close()
        except Exception:
            pass

    def _send_audio(self, pcm16: bytes) -> None:
        raise NotImplementedError

    def _send_finish(self) -> None:
        raise NotImplementedError

    def _handle_message(self, message: object) -> bool:
        raise NotImplementedError


def resample_pcm16(pcm16: bytes, source_rate: int, target_rate: int) -> bytes:
    if source_rate == target_rate:
        return pcm16
    samples = np.frombuffer(pcm16, dtype="<i2").astype(np.float64)
    if not len(samples):
        return b""
    output_size = max(1, round(len(samples) * target_rate / source_rate))
    positions = np.linspace(0, len(samples) - 1, output_size)
    converted = np.interp(positions, np.arange(len(samples)), samples)
    return np.clip(converted, -32768, 32767).astype("<i2").tobytes()


def validation_pcm16() -> bytes:
    """A short, quiet in-memory tone so providers validate actual PCM input."""

    sample_rate = 16_000
    positions = np.arange(sample_rate // 5, dtype=np.float64)
    samples = np.sin(2 * np.pi * 440 * positions / sample_rate) * 1_200
    return samples.astype("<i2").tobytes()


def map_service_error(
    error: object,
    *,
    credential_name: str = "访问凭据",
) -> SpeechRecognitionError:
    """Map provider/SDK failures without echoing secrets or raw responses."""

    message = str(error)
    folded = message.casefold()
    if any(
        value in folded
        for value in (
            "401",
            "403",
            "unauthorized",
            "forbidden",
            "authentication",
            "signature",
            "invalid token",
            "invalidapikey",
            "invalid_api_key",
            "access denied",
        )
    ):
        return SpeechRecognitionError(
            "authentication",
            f"语音识别认证失败，请检查{credential_name}和服务权限",
        )
    if any(
        value in folded
        for value in ("model not", "engine", "resource", "not found", "404")
    ):
        return SpeechRecognitionError(
            "configuration",
            "语音识别模型、引擎或资源 ID 不存在，或尚未开通",
        )
    if any(value in folded for value in ("429", "quota", "limit exceeded")):
        return SpeechRecognitionError("quota", "语音识别额度不足或请求过多")
    if any(value in folded for value in ("timeout", "timed out")):
        return SpeechRecognitionError("network", "语音识别服务连接超时")
    if any(value in folded for value in ("handshake", "protocol", "websocket")):
        return SpeechRecognitionError(
            "protocol",
            "语音识别协议连接失败，请检查服务地址和网络环境",
        )
    return SpeechRecognitionError(
        "service",
        "语音识别连接失败，请检查网络、服务权限和档案配置",
    )
