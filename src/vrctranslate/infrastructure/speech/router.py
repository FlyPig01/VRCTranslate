from __future__ import annotations

from collections.abc import Callable

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.application.speech_profiles import speech_service_descriptor
from vrctranslate.domain.speech import (
    SpeechProfileValidationResult,
    SpeechRecognitionError,
    SpeechRecognitionRequest,
    SpeechRecognitionResult,
    SpeechServiceCapabilities,
    SpeechStreamConfig,
    SpeechStreamEvent,
)


class SpeechRecognitionRouter:
    def __init__(self, adapters: list[object]) -> None:
        self._adapters = {
            str(getattr(adapter, "provider")): adapter for adapter in adapters
        }

    def capabilities(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechServiceCapabilities:
        adapter = self._adapters.get(profile.provider)
        if adapter is not None and hasattr(adapter, "capabilities"):
            return adapter.capabilities(profile)
        descriptor = speech_service_descriptor(profile.provider)
        if descriptor is None:
            raise SpeechRecognitionError(
                "configuration", f"当前版本不支持语音服务：{profile.provider}"
            )
        return descriptor.capabilities

    def open_session(
        self,
        profile: SpeechRecognitionProfile,
        config: SpeechStreamConfig,
        on_event: Callable[[SpeechStreamEvent], None],
        on_error: Callable[[Exception], None],
    ):
        capabilities = self.capabilities(profile)
        if not capabilities.realtime_eligible:
            raise SpeechRecognitionError(
                "configuration", "该语音档案不是流式识别服务"
            )
        adapter = self._adapters.get(profile.provider)
        if adapter is None or not hasattr(adapter, "open_session"):
            raise SpeechRecognitionError(
                "configuration", "当前版本没有实现该实时语音协议"
            )
        return adapter.open_session(profile, config, on_event, on_error)

    def validate_profile(
        self, profile: SpeechRecognitionProfile
    ) -> SpeechProfileValidationResult:
        capabilities = self.capabilities(profile)
        if not capabilities.caption_eligible:
            return SpeechProfileValidationResult(
                "incompatible", "该档案属于旧协议，当前版本不支持启动"
            )
        adapter = self._adapters.get(profile.provider)
        if adapter is None or not hasattr(adapter, "validate_profile"):
            raise SpeechRecognitionError(
                "configuration", "当前版本没有实现该服务的连接验证"
            )
        return adapter.validate_profile(profile)

    def transcribe(
        self,
        request: SpeechRecognitionRequest,
        profile: SpeechRecognitionProfile,
    ) -> SpeechRecognitionResult:
        adapter = self._adapters.get(profile.provider)
        if adapter is None or not hasattr(adapter, "transcribe"):
            raise SpeechRecognitionError(
                "configuration",
                f"当前版本不支持语音识别类型：{profile.provider}",
            )
        return adapter.transcribe(request, profile)

    def release(self, profile: SpeechRecognitionProfile) -> None:
        adapter = self._adapters.get(profile.provider)
        if adapter is not None and hasattr(adapter, "release"):
            adapter.release(profile)
