from __future__ import annotations

import httpx

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import TranslationCapabilities
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult
from vrctranslate.infrastructure.translation.llm_prompt import (
    build_translation_messages,
)


class OpenAICompatibleTranslator:
    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="openai_compatible",
            display_name="OpenAI 兼容接口",
            online=True,
            supports_auto_detect=True,
            supports_batch=False,
            realtime_recommended=False,
            requires_api_key=True,
            glossary_mode="prompt",
        )

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        if not profile.api_key.strip():
            raise TranslationError("configuration", "未填写翻译服务 API 密钥")
        if not profile.model.strip():
            raise TranslationError("configuration", "未填写翻译模型名称")
        if not profile.base_url.strip():
            raise TranslationError("configuration", "未填写翻译服务地址")

        endpoint = f"{profile.base_url.rstrip('/')}/chat/completions"
        payload = {
            "model": profile.model,
            "temperature": 0,
            "messages": build_translation_messages(request),
        }
        try:
            with httpx.Client(timeout=profile.timeout_seconds) as client:
                response = client.post(
                    endpoint,
                    headers={
                        "Authorization": f"Bearer {profile.api_key}",
                        "Content-Type": "application/json",
                    },
                    json=payload,
                )
            response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise TranslationError("network", "翻译请求超时，请稍后重试") from exc
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            if status in (401, 403):
                category, message = "authentication", "翻译服务认证失败，请检查 API 密钥"
            elif status == 429:
                category, message = "quota", "翻译服务请求过多或额度不足"
            else:
                category, message = "service", f"翻译服务返回 HTTP {status}"
            raise TranslationError(category, message) from exc
        except httpx.HTTPError as exc:
            raise TranslationError("network", "无法连接翻译服务") from exc

        try:
            data = response.json()
            translated = normalize_text(str(data["choices"][0]["message"]["content"]))
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            raise TranslationError("response", "翻译服务返回了无法识别的数据") from exc
        if not translated:
            raise TranslationError("response", "翻译服务返回了空译文")
        return TranslationResult(
            request_id=request.request_id,
            original=normalize_text(request.text),
            translated=translated,
            source_language=request.source_language,
            target_language=request.target_language,
            purpose=request.purpose,
        )
