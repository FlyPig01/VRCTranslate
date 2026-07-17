from __future__ import annotations

import httpx

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import TranslationCapabilities
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


_TARGET_LANGUAGE_CODES = {
    "zh-CN": "ZH-HANS",
    "zh-TW": "ZH-HANT",
    "en": "EN",
    "ja": "JA",
    "ko": "KO",
    "fr": "FR",
    "de": "DE",
    "es": "ES",
    "ru": "RU",
}

_SOURCE_LANGUAGE_CODES = {
    **_TARGET_LANGUAGE_CODES,
    "zh-CN": "ZH",
    "zh-TW": "ZH",
}


class DeepLTranslator:
    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="deepl",
            display_name="DeepL API",
            online=True,
            supports_auto_detect=True,
            supports_batch=True,
            realtime_recommended=True,
            requires_api_key=True,
            supported_languages=tuple(_TARGET_LANGUAGE_CODES),
        )

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        return self.translate_batch([request], profile)[0]

    def translate_batch(
        self,
        requests: list[TranslationRequest],
        profile: TranslationProfile,
    ) -> list[TranslationResult]:
        if not requests:
            return []
        if not profile.api_key.strip():
            raise TranslationError("configuration", "未填写 DeepL API 密钥")
        target = _TARGET_LANGUAGE_CODES.get(requests[0].target_language)
        if target is None:
            raise TranslationError("configuration", "DeepL 不支持当前目标语言")
        if any(
            request.target_language != requests[0].target_language
            or request.source_language != requests[0].source_language
            for request in requests
        ):
            raise TranslationError("configuration", "DeepL 批量请求的语言方向必须一致")
        endpoint = profile.base_url.strip() or (
            "https://api-free.deepl.com/v2/translate"
            if profile.api_key.strip().endswith(":fx")
            else "https://api.deepl.com/v2/translate"
        )
        data: dict[str, str | list[str]] = {
            "text": [normalize_text(request.text) for request in requests],
            "target_lang": target,
        }
        if requests[0].source_language != "auto":
            source = _SOURCE_LANGUAGE_CODES.get(requests[0].source_language)
            if source:
                data["source_lang"] = source
        try:
            with httpx.Client(timeout=profile.timeout_seconds) as client:
                response = client.post(
                    endpoint,
                    headers={"Authorization": f"DeepL-Auth-Key {profile.api_key}"},
                    data=data,
                )
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise TranslationError("network", "DeepL 翻译请求超时") from exc
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            if status in (401, 403):
                message, category = "DeepL 认证失败，请检查 API 密钥", "authentication"
            elif status == 456:
                message, category = "DeepL 翻译额度已用尽", "quota"
            elif status == 429:
                message, category = "DeepL 请求过多，请稍后重试", "quota"
            else:
                message, category = f"DeepL 返回 HTTP {status}", "service"
            raise TranslationError(category, message) from exc
        except httpx.HTTPError as exc:
            raise TranslationError("network", "无法连接 DeepL") from exc
        try:
            translations = response.json()["translations"]
            translated_texts = [normalize_text(item["text"]) for item in translations]
            if len(translated_texts) != len(requests):
                raise ValueError("translation count mismatch")
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            raise TranslationError("response", "DeepL 返回了无法识别的数据") from exc
        return [
            TranslationResult(
                request.request_id,
                normalize_text(request.text),
                translated,
                request.source_language,
                request.target_language,
                request.purpose,
            )
            for request, translated in zip(requests, translated_texts, strict=True)
        ]
