from __future__ import annotations

from html.parser import HTMLParser
import random
import time
from threading import Lock

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

_DEFAULT_JSON_ENDPOINT = "https://translate.googleapis.com/translate_a/single"
_KNOWN_JSON_ENDPOINTS = frozenset(
    {
        _DEFAULT_JSON_ENDPOINT,
        "https://translate.google.com/translate_a/single",
    }
)
_MOBILE_FALLBACK_ENDPOINT = "https://translate.google.com/m"
_MOBILE_FALLBACK_SECONDS = 15 * 60
_FAILURE_THRESHOLD = 3
_FAILURE_COOLDOWN_SECONDS = 5 * 60


class _MobileResultParser(HTMLParser):
    """Extract the translated text without depending on an HTML package."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self._inside_result = False
        self._div_depth = 0
        self.parts: list[str] = []

    def handle_starttag(
        self,
        tag: str,
        attrs: list[tuple[str, str | None]],
    ) -> None:
        if self._inside_result:
            if tag == "div":
                self._div_depth += 1
            return
        if tag != "div":
            return
        classes = dict(attrs).get("class", "") or ""
        if "result-container" in classes.split():
            self._inside_result = True
            self._div_depth = 1

    def handle_endtag(self, tag: str) -> None:
        if not self._inside_result or tag != "div":
            return
        self._div_depth -= 1
        if self._div_depth <= 0:
            self._inside_result = False

    def handle_data(self, data: str) -> None:
        if self._inside_result:
            self.parts.append(data)


class GoogleFreeTranslator:
    def __init__(self) -> None:
        self._mobile_fallback_until = 0.0
        self._consecutive_failures = 0
        self._unavailable_until = 0.0
        self._state_lock = Lock()

    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="google_free",
            display_name="Google 翻译（免费）",
            online=True,
            supports_auto_detect=True,
            supports_batch=False,
            realtime_recommended=False,
            requires_api_key=False,
            glossary_mode="local_placeholder",
            supported_languages=tuple(_LANGUAGE_CODES),
        )

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        self._ensure_available()
        target = _LANGUAGE_CODES.get(request.target_language)
        if target is None:
            raise TranslationError("configuration", "Google 翻译不支持当前目标语言")
        source = "auto"
        if request.source_language != "auto":
            source = _LANGUAGE_CODES.get(request.source_language, "auto")
        text = normalize_text(request.text)
        configured_endpoint = profile.base_url.strip()
        endpoint = configured_endpoint or _DEFAULT_JSON_ENDPOINT
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
        can_use_fallback = endpoint.rstrip("/") in _KNOWN_JSON_ENDPOINTS
        if can_use_fallback and time.monotonic() < self._mobile_fallback_until:
            try:
                translated_text = self._translate_with_mobile_page(
                    source,
                    target,
                    text,
                    profile.timeout_seconds,
                )
            except TranslationError:
                self._record_failure()
                raise
            self._record_success()
            return self._result(request, text, translated_text, request.source_language)

        try:
            with httpx.Client(timeout=profile.timeout_seconds, verify=False) as client:
                response = client.get(endpoint, params=params, headers=headers)
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            primary_error = TranslationError("network", "Google 翻译请求超时")
            return self._fallback_or_raise(
                request,
                text,
                source,
                target,
                profile.timeout_seconds,
                can_use_fallback,
                primary_error,
                exc,
            )
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            if status == 429:
                message, category = "Google 翻译请求过于频繁，请稍后重试", "quota"
            elif status in (403, 401):
                message, category = "Google 翻译访问被拒绝", "authentication"
            else:
                message, category = f"Google 翻译返回 HTTP {status}", "service"
            return self._fallback_or_raise(
                request,
                text,
                source,
                target,
                profile.timeout_seconds,
                can_use_fallback,
                TranslationError(category, message),
                exc,
            )
        except httpx.HTTPError as exc:
            return self._fallback_or_raise(
                request,
                text,
                source,
                target,
                profile.timeout_seconds,
                can_use_fallback,
                TranslationError("network", "无法连接 Google 翻译服务"),
                exc,
            )
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
            return self._fallback_or_raise(
                request,
                text,
                source,
                target,
                profile.timeout_seconds,
                can_use_fallback,
                TranslationError("response", "Google 翻译返回了无法识别的数据"),
                exc,
            )
        detected_source = request.source_language
        if source == "auto" and len(data) >= 3 and isinstance(data[2], str):
            detected_code = data[2]
            for code in _LANGUAGE_CODES:
                if _LANGUAGE_CODES[code].lower() == detected_code.lower():
                    detected_source = code
                    break
        self._record_success()
        return self._result(request, text, translated_text, detected_source)

    def _fallback_or_raise(
        self,
        request: TranslationRequest,
        text: str,
        source: str,
        target: str,
        timeout_seconds: float,
        can_use_fallback: bool,
        primary_error: TranslationError,
        cause: Exception,
    ) -> TranslationResult:
        if not can_use_fallback:
            self._record_failure()
            raise primary_error from cause
        try:
            translated_text = self._translate_with_mobile_page(
                source,
                target,
                text,
                timeout_seconds,
            )
        except TranslationError as fallback_error:
            self._record_failure()
            raise TranslationError(
                fallback_error.category,
                (
                    f"{fallback_error.user_message}；"
                    f"原免费接口同时失败：{primary_error.user_message}"
                ),
            ) from fallback_error
        self._mobile_fallback_until = time.monotonic() + _MOBILE_FALLBACK_SECONDS
        self._record_success()
        return self._result(request, text, translated_text, request.source_language)

    def _ensure_available(self) -> None:
        with self._state_lock:
            remaining = self._unavailable_until - time.monotonic()
        if remaining > 0:
            raise TranslationError(
                "service",
                "Google 免费翻译连续失败，已进入临时冷却，请稍后重试或更换翻译档案",
            )

    def _record_failure(self) -> None:
        with self._state_lock:
            self._consecutive_failures += 1
            if self._consecutive_failures >= _FAILURE_THRESHOLD:
                self._unavailable_until = (
                    time.monotonic() + _FAILURE_COOLDOWN_SECONDS
                )

    def _record_success(self) -> None:
        with self._state_lock:
            self._consecutive_failures = 0
            self._unavailable_until = 0.0

    @staticmethod
    def _translate_with_mobile_page(
        source: str,
        target: str,
        text: str,
        timeout_seconds: float,
    ) -> str:
        params = {"sl": source, "tl": target, "q": text, "hl": "en-US"}
        headers = {
            "User-Agent": random.choice(_USER_AGENTS),
            "Accept": "text/html,application/xhtml+xml",
        }
        try:
            with httpx.Client(timeout=timeout_seconds, verify=False) as client:
                response = client.get(
                    _MOBILE_FALLBACK_ENDPOINT,
                    params=params,
                    headers=headers,
                )
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise TranslationError("network", "Google 免费备用翻译请求超时") from exc
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            if status == 429:
                message, category = "Google 免费翻译已被限流，请稍后重试", "quota"
            elif status in (401, 403):
                message, category = "Google 免费翻译访问被拒绝", "authentication"
            else:
                message, category = f"Google 免费备用翻译返回 HTTP {status}", "service"
            raise TranslationError(category, message) from exc
        except httpx.HTTPError as exc:
            raise TranslationError("network", "无法连接 Google 免费备用翻译") from exc

        parser = _MobileResultParser()
        try:
            parser.feed(response.text)
            translated_text = normalize_text("".join(parser.parts))
        except (ValueError, TypeError) as exc:
            raise TranslationError(
                "response",
                "Google 免费备用翻译返回了无法识别的数据",
            ) from exc
        if not translated_text:
            raise TranslationError(
                "response",
                "Google 免费备用翻译未返回译文，网页接口可能已变更",
            )
        return translated_text

    @staticmethod
    def _result(
        request: TranslationRequest,
        text: str,
        translated_text: str,
        detected_source: str,
    ) -> TranslationResult:
        return TranslationResult(
            request.request_id,
            text,
            translated_text,
            detected_source,
            request.target_language,
            request.purpose,
        )
