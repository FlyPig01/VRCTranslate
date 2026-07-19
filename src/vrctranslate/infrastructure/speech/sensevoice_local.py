from __future__ import annotations

import gc
import importlib
import os
import re
import sys
from dataclasses import replace
from pathlib import Path
from threading import RLock
from typing import Any

import numpy as np

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.application.ports.speech_models import (
    SpeechModelManagement,
    SpeechModelPaths,
)
from vrctranslate.application.speech_profiles import speech_service_descriptor
from vrctranslate.domain.speech import (
    SpeechProfileValidationResult,
    SpeechRecognitionError,
    SpeechRecognitionRequest,
    SpeechRecognitionResult,
    SpeechServiceCapabilities,
)


_LANGUAGE_CODES = {
    "<|zh|>": "zh-CN",
    "<|en|>": "en",
    "<|ja|>": "ja",
    "<|ko|>": "ko",
    "<|yue|>": "yue",
}
_CONTROL_TOKEN = re.compile(r"<\|[^|>]+\|>")
_CONSERVATIVE_ALIASES = (
    (re.compile(r"(?<![A-Za-z])VR\s*Chat+t?(?![A-Za-z])", re.IGNORECASE), "VRChat"),
    (re.compile(r"(?<![A-Za-z])chat\s*box(?![A-Za-z])", re.IGNORECASE), "Chatbox"),
    (re.compile(r"(?<![A-Za-z])(?:physical|fs)\s+bones?(?![A-Za-z])", re.IGNORECASE), "PhysBone"),
    (re.compile(r"(?<![A-Za-z])fall+back\s+avatars?(?![A-Za-z])", re.IGNORECASE), "Fallback Avatar"),
    (re.compile(r"(?<![A-Za-z])a(?:vv|lv)?atar\s+contacts?(?![A-Za-z])", re.IGNORECASE), "Avatar Contact"),
    (re.compile(r"(?<![A-Za-z])expressions?\s+menus?(?![A-Za-z])", re.IGNORECASE), "Expressions Menu"),
    (re.compile(r"(?<![A-Za-z])public\s+instances?(?![A-Za-z])", re.IGNORECASE), "Public Instance"),
    (re.compile(r"\bjoining\s+the\s+public\s+In\.", re.IGNORECASE), "joining the Public Instance"),
    (re.compile(r"\bUan\b"), "Udon"),
    (re.compile(r"U荡(?=脚本)"), "Udon"),
    (re.compile(r"アルチャット"), "VRChat"),
    (re.compile(r"うどん(?=\s*スクリプト)"), "Udon"),
    (re.compile(r"エクスプレッションズ\s*メニュー"), "Expressions Menu"),
    (re.compile(r"アバター\s*コンタクト"), "Avatar Contact"),
    (re.compile(r"エスボ(?=設定)"), "PhysBone"),
    (re.compile(r"チャットシャトーボックス"), "Chatbox"),
    (re.compile(r"パブリック\s*インスタンス"), "Public Instance"),
    (re.compile(r"フォール\s*バック\s*アバター"), "Fallback Avatar"),
)


class SenseVoiceLocalSpeechRecognizer:
    """Portable CPU-only SenseVoice adapter loaded outside the main environment."""

    provider = "local_offline"

    def __init__(self, components: SpeechModelManagement, *, num_threads: int = 4) -> None:
        self._components = components
        self._num_threads = max(1, int(num_threads))
        self._lock = RLock()
        self._recognizer: Any | None = None
        self._runtime_module: Any | None = None
        self._dll_handle: Any | None = None

    def capabilities(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechServiceCapabilities:
        descriptor = speech_service_descriptor(self.provider)
        if descriptor is None:
            raise SpeechRecognitionError("configuration", "缺少本地语音服务目录")
        return replace(descriptor.capabilities)

    def validate_profile(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechProfileValidationResult:
        try:
            self._validate_profile_selection(profile)
            paths = self._components.verify()
            self._ensure_recognizer(paths)
        except (OSError, ImportError, RuntimeError, SpeechRecognitionError) as exc:
            return SpeechProfileValidationResult("failed", self._safe_message(exc))
        return SpeechProfileValidationResult(
            "verified", "SenseVoiceSmall INT8 本地模型与运行库校验通过"
        )

    def transcribe(
        self,
        request: SpeechRecognitionRequest,
        profile: SpeechRecognitionProfile,
    ) -> SpeechRecognitionResult:
        self._validate_profile_selection(profile)
        if request.sample_rate != 16_000:
            raise SpeechRecognitionError("audio", "本地语音识别只接受 16 kHz 音频")
        pcm16 = request.pcm16[: len(request.pcm16) - len(request.pcm16) % 2]
        if not pcm16:
            return SpeechRecognitionResult(request.request_id, "", "")
        samples = np.frombuffer(pcm16, dtype="<i2").astype(np.float32)
        samples *= 1.0 / 32768.0
        try:
            with self._lock:
                recognizer = self._ensure_recognizer()
                stream = recognizer.create_stream()
                stream.accept_waveform(16_000, samples)
                recognizer.decode_stream(stream)
                result = stream.result
        except SpeechRecognitionError:
            raise
        except Exception as exc:
            raise SpeechRecognitionError(
                "local_runtime", "本地语音识别失败，请校验或重新安装模型"
            ) from exc
        text = self._normalize_text(str(getattr(result, "text", "")))
        language = _LANGUAGE_CODES.get(
            str(getattr(result, "lang", "")).strip(), ""
        )
        return SpeechRecognitionResult(request.request_id, text, language)

    def release(self, profile: SpeechRecognitionProfile) -> None:
        if profile.provider != self.provider:
            return
        with self._lock:
            self._recognizer = None
        gc.collect()

    def _validate_profile_selection(self, profile: SpeechRecognitionProfile) -> None:
        if profile.provider != self.provider:
            raise SpeechRecognitionError("configuration", "语音档案类型不匹配")
        if profile.model != "sensevoice-small-int8":
            raise SpeechRecognitionError("configuration", "不支持所选本地语音模型")

    def _ensure_recognizer(
        self, verified_paths: SpeechModelPaths | None = None
    ) -> Any:
        with self._lock:
            if self._recognizer is not None:
                return self._recognizer
            try:
                paths = verified_paths or self._components.verify()
            except (OSError, FileNotFoundError) as exc:
                raise SpeechRecognitionError(
                    "model_missing", "SenseVoice 本地语音模型未安装或校验失败"
                ) from exc
            module = self._load_runtime(paths.runtime_root)
            try:
                self._recognizer = module.OfflineRecognizer.from_sense_voice(
                    model=str(paths.model),
                    tokens=str(paths.tokens),
                    num_threads=self._num_threads,
                    language="auto",
                    use_itn=True,
                    provider="cpu",
                )
            except Exception as exc:
                raise SpeechRecognitionError(
                    "model_load", "SenseVoice 本地语音模型加载失败"
                ) from exc
            return self._recognizer

    def _load_runtime(self, root: Path) -> Any:
        if self._runtime_module is not None:
            return self._runtime_module
        package = root / "sherpa_onnx"
        library = package / "lib"
        if not package.is_dir() or not library.is_dir():
            raise SpeechRecognitionError("model_missing", "本地语音运行库不完整")
        root_text = str(root)
        if root_text not in sys.path:
            sys.path.insert(0, root_text)
        if os.name == "nt" and hasattr(os, "add_dll_directory"):
            self._dll_handle = os.add_dll_directory(str(library))
        try:
            self._runtime_module = importlib.import_module("sherpa_onnx")
        except (ImportError, OSError) as exc:
            raise SpeechRecognitionError(
                "local_runtime", "本地语音运行库无法加载，请重新安装组件"
            ) from exc
        return self._runtime_module

    @staticmethod
    def _normalize_text(value: str) -> str:
        text = _CONTROL_TOKEN.sub("", value).strip()
        for pattern, replacement in _CONSERVATIVE_ALIASES:
            text = pattern.sub(replacement, text)
        return text

    @staticmethod
    def _safe_message(error: Exception) -> str:
        if isinstance(error, SpeechRecognitionError):
            return error.user_message
        if isinstance(error, OSError):
            return "SenseVoice 本地语音模型未安装或校验失败"
        return "SenseVoice 本地语音组件验证失败"
