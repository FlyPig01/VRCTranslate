from __future__ import annotations

from urllib.parse import urlsplit

from Tea.exceptions import TeaException
from alibabacloud_alimt20181012 import models as alimt_models
from alibabacloud_alimt20181012.client import Client as AlimtClient
from alibabacloud_tea_openapi import models as open_api_models
from alibabacloud_tea_util import models as util_models

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import TranslationCapabilities
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


_LANGUAGE_CODES = {
    "zh-CN": "zh",
    "zh-TW": "zh-tw",
    "en": "en",
    "ja": "ja",
    "ko": "ko",
    "fr": "fr",
    "de": "de",
    "es": "es",
    "ru": "ru",
}
_MAX_TEXT_LENGTH = 5000


class AliyunTranslator:
    """Alibaba Cloud Machine Translation through its official Tea SDK."""

    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="aliyun",
            display_name="阿里云机器翻译",
            online=True,
            supports_auto_detect=True,
            supports_batch=False,
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
        access_key_id = profile.api_key.strip()
        access_key_secret = profile.model.strip()
        if not access_key_id or not access_key_secret:
            raise TranslationError(
                "configuration",
                "未填写阿里云 AccessKey ID 和 AccessKey Secret",
            )
        target = _LANGUAGE_CODES.get(request.target_language)
        if target is None:
            raise TranslationError("configuration", "阿里云翻译不支持当前目标语言")
        source = (
            "auto"
            if request.source_language == "auto"
            else _LANGUAGE_CODES.get(request.source_language)
        )
        if source is None:
            raise TranslationError("configuration", "阿里云翻译不支持当前源语言")
        text = normalize_text(request.text)
        if len(text) > _MAX_TEXT_LENGTH:
            raise TranslationError(
                "configuration",
                f"阿里云翻译单次文本不能超过 {_MAX_TEXT_LENGTH} 个字符",
            )

        region = profile.region.strip()
        if not region:
            raise TranslationError(
                "configuration",
                "请选择阿里云机器翻译资源所在地域",
            )
        endpoint = self._endpoint(profile.base_url)
        timeout_ms = max(1000, round(profile.timeout_seconds * 1000))
        config = open_api_models.Config(
            access_key_id=access_key_id,
            access_key_secret=access_key_secret,
            region_id=region,
            connect_timeout=timeout_ms,
            read_timeout=timeout_ms,
        )
        if endpoint:
            config.endpoint = endpoint
        runtime = util_models.RuntimeOptions(
            autoretry=False,
            max_attempts=1,
            connect_timeout=timeout_ms,
            read_timeout=timeout_ms,
        )
        api_mode = str(profile.options.get("aliyun_api", "general"))
        try:
            client = AlimtClient(config)
            if api_mode == "professional":
                sdk_request = alimt_models.TranslateRequest(
                    format_type="text",
                    scene="general",
                    source_language=source,
                    source_text=text,
                    target_language=target,
                )
                response = client.translate_with_options(sdk_request, runtime)
            else:
                sdk_request = alimt_models.TranslateGeneralRequest(
                    format_type="text",
                    scene="general",
                    source_language=source,
                    source_text=text,
                    target_language=target,
                )
                response = client.translate_general_with_options(
                    sdk_request,
                    runtime,
                )
        except TeaException as exc:
            raise self._sdk_error(exc) from exc
        except (TimeoutError, ConnectionError, OSError) as exc:
            raise TranslationError("network", "无法连接阿里云机器翻译服务") from exc
        except Exception as exc:
            raise TranslationError(
                "service",
                f"阿里云翻译 SDK 调用失败：{type(exc).__name__}",
            ) from exc

        body = getattr(response, "body", None)
        code = getattr(body, "code", None)
        message = str(getattr(body, "message", "") or "")
        normalized_code = str(code).strip() if code is not None else ""
        if normalized_code not in {"", "200"}:
            raise self._service_error(normalized_code, message)
        data = getattr(body, "data", None)
        translated_text = normalize_text(str(getattr(data, "translated", "") or ""))
        if not translated_text:
            raise TranslationError("response", "阿里云翻译未返回有效译文")
        detected = request.source_language
        if source == "auto":
            detected = self._local_language(
                str(getattr(data, "detected_language", "") or "")
            )
        return TranslationResult(
            request.request_id,
            text,
            translated_text,
            detected,
            request.target_language,
            request.purpose,
        )

    def translate_batch(
        self,
        requests: list[TranslationRequest],
        profile: TranslationProfile,
    ) -> list[TranslationResult]:
        return [self.translate(request, profile) for request in requests]

    @staticmethod
    def _endpoint(value: str) -> str:
        endpoint = value.strip().rstrip("/")
        if not endpoint:
            return ""
        if "://" in endpoint:
            parsed = urlsplit(endpoint)
            if parsed.scheme not in {"http", "https"} or not parsed.hostname:
                raise TranslationError("configuration", "阿里云翻译端点格式不正确")
            if parsed.path not in {"", "/"} or parsed.query or parsed.fragment:
                raise TranslationError("configuration", "阿里云翻译端点只能填写主机名")
            endpoint = parsed.netloc
        if "/" in endpoint or not endpoint:
            raise TranslationError("configuration", "阿里云翻译端点只能填写主机名")
        return endpoint

    @staticmethod
    def _local_language(value: str) -> str:
        normalized = value.casefold()
        for local, remote in _LANGUAGE_CODES.items():
            if remote.casefold() == normalized:
                return local
        return "auto"

    @classmethod
    def _sdk_error(cls, error: TeaException) -> TranslationError:
        code = str(getattr(error, "code", "") or "")
        message = str(getattr(error, "message", "") or "")
        if not code and any(
            token in message.casefold()
            for token in ("timeout", "timed out", "connection", "network")
        ):
            return TranslationError("network", "阿里云翻译请求超时或网络连接失败")
        return cls._service_error(code or "SDKError", message)

    @staticmethod
    def _service_error(code: str, message: str) -> TranslationError:
        value = f"{code} {message}".casefold()
        if any(
            token in value
            for token in (
                "invalidaccesskey",
                "signature",
                "unauthorized",
                "forbidden",
                "accessdenied",
                "permission",
            )
        ):
            category = "authentication"
            user_message = "阿里云认证失败，请检查 AccessKey 和服务权限"
        elif any(
            token in value
            for token in ("throttl", "quota", "limit", "balance", "arrears")
        ):
            category = "quota"
            user_message = "阿里云翻译额度不足或请求过于频繁"
        elif any(
            token in value
            for token in ("invalidparameter", "language", "region", "endpoint")
        ):
            category = "configuration"
            user_message = "阿里云翻译参数、语言或地域配置不正确"
        else:
            category = "service"
            user_message = "阿里云机器翻译服务调用失败"
        detail = " - ".join(part for part in (code, message) if part)
        return TranslationError(
            category,
            f"{user_message}：{detail}" if detail else user_message,
        )
