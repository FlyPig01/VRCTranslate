from __future__ import annotations

import html

import httpx

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import TranslationCapabilities
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


_LANGUAGE_CODES = {
    "zh-CN": "zh-CN",
    "zh-TW": "zh-TW",
    "en": "en",
    "ja": "ja",
    "ko": "ko",
    "fr": "fr",
    "de": "de",
    "es": "es",
    "ru": "ru",
}


class GoogleCloudTranslator:
    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="google_cloud",
            display_name="Google Cloud Translation",
            online=True,
            supports_auto_detect=True,
            supports_batch=True,
            realtime_recommended=True,
            requires_api_key=True,
            glossary_mode="local_placeholder",
            supported_languages=tuple(_LANGUAGE_CODES),
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
            raise TranslationError("configuration", "未填写 Google Cloud API 密钥")
        target = _LANGUAGE_CODES.get(requests[0].target_language)
        if target is None:
            raise TranslationError("configuration", "Google Cloud 不支持当前目标语言")
        if any(
            request.target_language != requests[0].target_language
            or request.source_language != requests[0].source_language
            for request in requests
        ):
            raise TranslationError("configuration", "Google 批量请求的语言方向必须一致")
        endpoint = (
            profile.base_url.strip()
            or "https://translation.googleapis.com/language/translate/v2"
        )
        payload: dict[str, object] = {
            "q": [normalize_text(request.text) for request in requests],
            "target": target,
            "format": "text",
        }
        if requests[0].source_language != "auto":
            source = _LANGUAGE_CODES.get(requests[0].source_language)
            if source:
                payload["source"] = source
        try:
            with httpx.Client(timeout=profile.timeout_seconds) as client:
                response = client.post(
                    endpoint,
                    params={"key": profile.api_key},
                    json=payload,
                )
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise TranslationError("network", "Google Cloud 翻译请求超时") from exc
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            if status in (401, 403):
                message, category = "Google Cloud 认证或权限失败", "authentication"
            elif status == 429:
                message, category = "Google Cloud 请求过多或额度不足", "quota"
            else:
                message, category = f"Google Cloud 返回 HTTP {status}", "service"
            raise TranslationError(category, message) from exc
        except httpx.HTTPError as exc:
            raise TranslationError("network", "无法连接 Google Cloud Translation") from exc
        try:
            translations = response.json()["data"]["translations"]
            translated_texts = [
                normalize_text(html.unescape(item["translatedText"]))
                for item in translations
            ]
            if len(translated_texts) != len(requests):
                raise ValueError("translation count mismatch")
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            raise TranslationError(
                "response", "Google Cloud 返回了无法识别的数据"
            ) from exc
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
