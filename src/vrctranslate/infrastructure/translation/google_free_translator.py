from __future__ import annotations

import random
import time

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

_USER_AGENTS = [
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
]


class GoogleFreeTranslator:
    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="google_free",
            display_name="Google 翻译（免费）",
            online=True,
            supports_auto_detect=True,
            supports_batch=False,
            realtime_recommended=True,
            requires_api_key=False,
            glossary_mode="local_placeholder",
            supported_languages=tuple(_LANGUAGE_CODES),
        )

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        target = _LANGUAGE_CODES.get(request.target_language)
        if target is None:
            raise TranslationError("configuration", "Google 翻译不支持当前目标语言")
        source = "auto"
        if request.source_language != "auto":
            source = _LANGUAGE_CODES.get(request.source_language, "auto")
        text = normalize_text(request.text)
        endpoint = profile.base_url.strip() or "https://translate.googleapis.com/translate_a/single"
        params = {
            "client": "gtx",
            "sl": source,
            "tl": target,
            "dt": "t",
            "q": text,
        }
        headers = {
            "User-Agent": random.choice(_USER_AGENTS),
            "Accept": "application/json",
        }
        try:
            with httpx.Client(timeout=profile.timeout_seconds, verify=False) as client:
                response = client.get(endpoint, params=params, headers=headers)
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise TranslationError("network", "Google 翻译请求超时") from exc
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            if status == 429:
                message, category = "Google 翻译请求过于频繁，请稍后重试", "quota"
            elif status in (403, 401):
                message, category = "Google 翻译访问被拒绝", "authentication"
            else:
                message, category = f"Google 翻译返回 HTTP {status}", "service"
            raise TranslationError(category, message) from exc
        except httpx.HTTPError as exc:
            raise TranslationError("network", "无法连接 Google 翻译服务") from exc
        try:
            data = response.json()
            if not isinstance(data, list) or not data or not isinstance(data[0], list):
                raise ValueError("invalid response structure")
            translated_parts = []
            for item in data[0]:
                if isinstance(item, list) and len(item) >= 1 and isinstance(item[0], str):
                    translated_parts.append(item[0])
            if not translated_parts:
                raise ValueError("no translation found")
            translated_text = normalize_text("".join(translated_parts))
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            raise TranslationError(
                "response", "Google 翻译返回了无法识别的数据"
            ) from exc
        detected_source = request.source_language
        if source == "auto" and len(data) >= 3 and isinstance(data[2], str):
            detected_code = data[2]
            for code in _LANGUAGE_CODES:
                if _LANGUAGE_CODES[code].lower() == detected_code.lower():
                    detected_source = code
                    break
        return TranslationResult(
            request.request_id,
            text,
            translated_text,
            detected_source,
            request.target_language,
            request.purpose,
        )
