from __future__ import annotations

from typing import Any

from vrctranslate.application.dto import (
    CONFIG_VERSION,
    AppSettings,
    SUPPORTED_TRANSLATION_PROVIDERS,
    TranslationProfile,
    TranslationRouteSettings,
    TranslationSettings,
    UiSettings,
)
from vrctranslate.infrastructure.settings.schema_v3 import (
    float_in_range,
    mapping,
    ocr_from_dict,
    osc_from_dict,
)


def migrate_v1(raw: dict[str, Any]) -> AppSettings:
    old_translation = mapping(raw.get("translation"))
    old_ui = mapping(raw.get("ui"))
    provider = str(old_translation.get("provider", "test"))
    if provider not in SUPPORTED_TRANSLATION_PROVIDERS:
        provider = "test"
    profile_id = "legacy-openai" if provider == "openai_compatible" else "test"
    profile_name = "旧 OpenAI 兼容配置" if provider == "openai_compatible" else "测试回显"
    profile = TranslationProfile(
        id=profile_id,
        name=profile_name,
        provider=provider,
        base_url=str(old_translation.get("base_url", "")),
        api_key=str(old_translation.get("api_key", "")),
        model=str(old_translation.get("model", "")),
        timeout_seconds=float_in_range(
            old_translation.get("timeout_seconds"), 8.0, 8, 120
        ),
    )
    source = str(old_ui.get("source_language", "auto"))
    target = str(old_ui.get("target_language", "zh-CN"))
    message_format = str(old_ui.get("message_format", "translation_only"))
    return AppSettings(
        version=CONFIG_VERSION,
        translation=TranslationSettings(
            profiles=[profile],
            self_route=TranslationRouteSettings(
                profile_id=profile_id,
                source_language=source,
                target_language=target,
                message_format=message_format,
                timeout_seconds=profile.timeout_seconds,
            ),
            ocr_route=TranslationRouteSettings(
                profile_id=profile_id,
                source_language="ja",
                target_language=target,
                timeout_seconds=min(profile.timeout_seconds, 4.0),
            ),
        ),
        osc=osc_from_dict(mapping(raw.get("osc"))),
        ocr=ocr_from_dict(mapping(raw.get("ocr"))),
        ui=UiSettings(),
    )
