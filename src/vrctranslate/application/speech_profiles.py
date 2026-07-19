from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.domain.speech import SpeechServiceCapabilities


@dataclass(frozen=True, slots=True)
class SpeechServiceDescriptor:
    provider: str
    vendor: str
    model_ids: tuple[str, ...]
    capabilities: SpeechServiceCapabilities
    creatable: bool = True


_SERVICES = {
    "tencent_realtime": SpeechServiceDescriptor(
        provider="tencent_realtime",
        vendor="tencent",
        model_ids=(
            "16k_zh",
            "16k_zh_en",
            "16k_multi_lang",
            "16k_zh-TW",
            "16k_zh_edu",
            "16k_zh_medical",
            "16k_zh_court",
            "16k_yue",
            "16k_en",
            "16k_en_large",
            "16k_en_game",
            "16k_en_edu",
            "16k_ja",
            "16k_ko",
            "16k_th",
            "16k_id",
            "16k_vi",
            "16k_ms",
            "16k_fil",
            "16k_pt",
            "16k_tr",
            "16k_ar",
            "16k_es",
            "16k_hi",
            "16k_fr",
            "16k_de",
        ),
        capabilities=SpeechServiceCapabilities(
            provider="tencent_realtime",
            streaming_audio=True,
            partial_transcript=True,
            final_transcript=True,
        ),
    ),
    "aliyun_nls_realtime": SpeechServiceDescriptor(
        provider="aliyun_nls_realtime",
        vendor="aliyun",
        model_ids=("nls-realtime",),
        capabilities=SpeechServiceCapabilities(
            provider="aliyun_nls_realtime",
            streaming_audio=True,
            partial_transcript=True,
            final_transcript=True,
        ),
    ),
}

def speech_service_descriptor(provider: str) -> SpeechServiceDescriptor | None:
    return _SERVICES.get(provider)


def creatable_speech_services() -> tuple[SpeechServiceDescriptor, ...]:
    return tuple(value for value in _SERVICES.values() if value.creatable)


def profile_fingerprint(profile: SpeechRecognitionProfile) -> str:
    options = {
        key: value
        for key, value in profile.options.items()
        if key not in {"validation_state", "validation_fingerprint", "validation_message"}
    }
    raw = json.dumps(
        {
            "provider": profile.provider,
            "base_url": profile.base_url,
            "api_key": profile.api_key,
            "model": profile.model,
            "options": options,
        },
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def profile_validation_state(profile: SpeechRecognitionProfile) -> str:
    descriptor = speech_service_descriptor(profile.provider)
    if descriptor is None or not descriptor.capabilities.realtime_eligible:
        return "incompatible"
    state = str(profile.options.get("validation_state", "pending"))
    if state == "verified" and profile.options.get("validation_fingerprint") != profile_fingerprint(profile):
        return "pending"
    return state if state in {"pending", "verified", "failed"} else "pending"


def set_profile_validation(
    profile: SpeechRecognitionProfile,
    state: str,
    message: str = "",
) -> None:
    profile.options["validation_state"] = state
    profile.options["validation_message"] = message
    if state == "verified":
        profile.options["validation_fingerprint"] = profile_fingerprint(profile)
    else:
        profile.options.pop("validation_fingerprint", None)
