from __future__ import annotations

from dataclasses import dataclass

from vrctranslate.application.dto import TranslationProfile


@dataclass(frozen=True, slots=True)
class TranslationQualityAdvice:
    state: str
    candidate_provider: str = ""
    candidate_variant: str = ""


_DIRECTION_CANDIDATES: dict[tuple[str, str], tuple[str, str]] = {
    ("zh-TW", "zh-CN"): ("aliyun", "professional"),
    ("zh-CN", "zh-TW"): ("aliyun", "general"),
    ("en", "zh-CN"): ("tencent", ""),
    ("zh-CN", "en"): ("aliyun", "general"),
    ("ja", "zh-CN"): ("tencent", ""),
    ("zh-CN", "ja"): ("aliyun", "professional"),
    ("ko", "zh-CN"): ("tencent", ""),
    ("zh-CN", "ko"): ("tencent", ""),
    ("fr", "zh-CN"): ("tencent", ""),
    ("zh-CN", "fr"): ("tencent", ""),
    ("de", "zh-CN"): ("tencent", ""),
    ("zh-CN", "de"): ("tencent", ""),
    ("es", "zh-CN"): ("tencent", ""),
    ("zh-CN", "es"): ("aliyun", "professional"),
    ("ru", "zh-CN"): ("tencent", ""),
    ("zh-CN", "ru"): ("aliyun", "general"),
}


def translation_quality_advice(
    profile: TranslationProfile | None,
    source_language: str,
    target_language: str,
) -> TranslationQualityAdvice:
    """Return advisory benchmark metadata without changing the active route."""

    if profile is None:
        return TranslationQualityAdvice("none")
    if profile.provider == "google_free":
        return TranslationQualityAdvice("experimental")
    if source_language == "auto" or source_language == target_language:
        return TranslationQualityAdvice("none")
    candidate = _DIRECTION_CANDIDATES.get((source_language, target_language))
    if candidate is None:
        return TranslationQualityAdvice("none")
    provider, variant = candidate
    current_variant = (
        str(profile.options.get("aliyun_api", "general"))
        if profile.provider == "aliyun"
        else ""
    )
    if profile.provider == provider and (not variant or current_variant == variant):
        return TranslationQualityAdvice("candidate", provider, variant)
    return TranslationQualityAdvice("alternative", provider, variant)
