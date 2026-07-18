from __future__ import annotations

import hashlib
import hmac
import json
import time
from datetime import datetime, timezone

import httpx

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import TranslationCapabilities
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


_LANGUAGE_CODES = {
    "zh-CN": "zh",
    "zh-TW": "zh-TW",
    "en": "en",
    "ja": "ja",
    "ko": "ko",
    "fr": "fr",
    "de": "de",
    "es": "es",
    "ru": "ru",
}

_SERVICE = "tmt"
_VERSION = "2018-03-21"
_ACTION = "TextTranslate"
_ALGORITHM = "TC3-HMAC-SHA256"


def _sha256_hex(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def _hmac_sha256(key: bytes, msg: str) -> bytes:
    return hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest()


class TencentTranslator:
    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="tencent",
            display_name="腾讯云翻译",
            online=True,
            supports_auto_detect=False,
            supports_batch=False,
            realtime_recommended=True,
            requires_api_key=True,
            glossary_mode="local_placeholder",
            supported_languages=tuple(_LANGUAGE_CODES),
        )

    def _get_region(self, profile: TranslationProfile) -> str:
        return profile.region.strip() or "ap-beijing"

    def _get_endpoint(self, profile: TranslationProfile) -> str:
        return profile.base_url.strip() or "tmt.tencentcloudapi.com"

    def _tc3_sign(
        self,
        secret_id: str,
        secret_key: str,
        service: str,
        host: str,
        action: str,
        version: str,
        region: str,
        payload: str,
        timestamp: int,
    ) -> dict[str, str]:
        date = datetime.fromtimestamp(timestamp, tz=timezone.utc).strftime("%Y-%m-%d")
        http_request_method = "POST"
        canonical_uri = "/"
        canonical_querystring = ""
        ct = "application/json; charset=utf-8"
        canonical_headers = (
            "content-type:" + ct + "\n"
            "host:" + host + "\n"
            "x-tc-action:" + action.lower() + "\n"
        )
        signed_headers = "content-type;host;x-tc-action"
        hashed_request_payload = _sha256_hex(payload)
        canonical_request = (
            http_request_method + "\n"
            + canonical_uri + "\n"
            + canonical_querystring + "\n"
            + canonical_headers + "\n"
            + signed_headers + "\n"
            + hashed_request_payload
        )
        credential_scope = date + "/" + service + "/" + "tc3_request"
        hashed_canonical_request = _sha256_hex(canonical_request)
        string_to_sign = (
            _ALGORITHM + "\n"
            + str(timestamp) + "\n"
            + credential_scope + "\n"
            + hashed_canonical_request
        )
        secret_date = _hmac_sha256(("TC3" + secret_key).encode("utf-8"), date)
        secret_service = _hmac_sha256(secret_date, service)
        secret_signing = _hmac_sha256(secret_service, "tc3_request")
        signature = hmac.new(
            secret_signing,
            string_to_sign.encode("utf-8"),
            hashlib.sha256,
        ).hexdigest()
        authorization = (
            _ALGORITHM + " "
            + "Credential=" + secret_id + "/" + credential_scope + ", "
            + "SignedHeaders=" + signed_headers + ", "
            + "Signature=" + signature
        )
        return {
            "Authorization": authorization,
            "Content-Type": ct,
            "Host": host,
            "X-TC-Action": action,
            "X-TC-Timestamp": str(timestamp),
            "X-TC-Version": version,
            "X-TC-Region": region,
        }

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        secret_id = profile.api_key.strip()
        secret_key = profile.model.strip()
        if not secret_id or not secret_key:
            raise TranslationError(
                "configuration",
                "未填写腾讯云 SecretId 和 SecretKey",
            )
        target = _LANGUAGE_CODES.get(request.target_language)
        if target is None:
            raise TranslationError("configuration", "腾讯翻译不支持当前目标语言")
        source = _LANGUAGE_CODES.get(request.source_language)
        if source is None:
            raise TranslationError(
                "configuration",
                "腾讯翻译不支持自动识别，请在翻译路由中明确指定源语言",
            )
        text = normalize_text(request.text)
        if len(text) > 6000:
            raise TranslationError("configuration", "腾讯翻译单次文本需低于 6000 字符")
        region = self._get_region(profile)
        host = self._get_endpoint(profile)
        payload_dict: dict[str, object] = {
            "SourceText": text,
            "Source": source,
            "Target": target,
            "ProjectId": 0,
        }
        payload = json.dumps(payload_dict, ensure_ascii=False)
        timestamp = int(time.time())
        headers = self._tc3_sign(
            secret_id, secret_key, _SERVICE, host,
            _ACTION, _VERSION, region, payload, timestamp,
        )
        url = f"https://{host}/"
        try:
            with httpx.Client(timeout=profile.timeout_seconds) as client:
                response = client.post(url, headers=headers, content=payload.encode("utf-8"))
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise TranslationError("network", "腾讯翻译请求超时") from exc
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            if status in (401, 403):
                message, category = "腾讯云认证失败，请检查 SecretId 和 SecretKey", "authentication"
            elif status == 429:
                message, category = "腾讯翻译请求过于频繁（限制 5 次/秒），请稍后重试", "quota"
            else:
                message, category = f"腾讯翻译返回 HTTP {status}", "service"
            raise TranslationError(category, message) from exc
        except httpx.HTTPError as exc:
            raise TranslationError("network", "无法连接腾讯云翻译服务") from exc
        try:
            data = response.json()
            if "Response" not in data:
                raise ValueError("missing Response")
            resp = data["Response"]
            if "Error" in resp:
                err = resp["Error"]
                code = err.get("Code", "")
                msg = err.get("Message", "")
                if "AuthFailure" in code or "InvalidCredential" in code:
                    category = "authentication"
                elif "LimitExceeded" in code or "RequestLimitExceeded" in code:
                    category = "quota"
                elif "ServiceIsolate" in code or "StopUsing" in code:
                    category = "quota"
                elif "NoFreeAmount" in code:
                    category = "quota"
                elif "UserNotRegistered" in code:
                    category = "configuration"
                else:
                    category = "service"
                raise TranslationError(category, f"腾讯翻译错误：{code} - {msg}")
            translated_text = normalize_text(resp.get("TargetText", ""))
            if not translated_text:
                raise ValueError("empty TargetText")
        except TranslationError:
            raise
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            raise TranslationError(
                "response", "腾讯翻译返回了无法识别的数据"
            ) from exc
        return TranslationResult(
            request.request_id,
            text,
            translated_text,
            source,
            request.target_language,
            request.purpose,
        )

    def translate_batch(
        self,
        requests: list[TranslationRequest],
        profile: TranslationProfile,
    ) -> list[TranslationResult]:
        return [self.translate(request, profile) for request in requests]
