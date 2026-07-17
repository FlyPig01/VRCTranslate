from __future__ import annotations

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import TranslationCapabilities
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult
from vrctranslate.infrastructure.translation.argos_model_manager import ArgosModelManager


_LANGUAGE_CODES = {
    "zh-CN": "zh",
    "zh-TW": "zh",
    "en": "en",
    "ja": "ja",
    "ko": "ko",
    "fr": "fr",
    "de": "de",
    "es": "es",
    "ru": "ru",
}


class ArgosTranslator:
    def __init__(self, model_manager: ArgosModelManager) -> None:
        self._models = model_manager

    def capabilities(self) -> TranslationCapabilities:
        return TranslationCapabilities(
            provider="argos",
            display_name="Argos Translate（本地离线）",
            online=False,
            supports_auto_detect=False,
            supports_batch=False,
            realtime_recommended=True,
            requires_api_key=False,
            supported_languages=tuple(_LANGUAGE_CODES),
            available=self._models.component_available,
        )

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        del profile
        text = normalize_text(request.text)
        source = request.source_language
        if source == "auto":
            source = _detect_source_language(text)
        source_code = _LANGUAGE_CODES.get(source)
        target_code = _LANGUAGE_CODES.get(request.target_language)
        if source_code is None or target_code is None:
            raise TranslationError("configuration", "Argos 不支持当前语言代码")
        if source_code == target_code:
            translated = text
        else:
            self._models.configure_environment()
            if not self._models.component_available:
                raise TranslationError(
                    "component", "当前版本未包含 Argos Translate 本地翻译组件"
                )
            try:
                from argostranslate import translate

                installed = {language.code: language for language in translate.get_installed_languages()}
                source_language = installed.get(source_code)
                target_language = installed.get(target_code)
                if source_language is None or target_language is None:
                    raise TranslationError(
                        "model",
                        f"尚未安装 Argos 模型：{source_code} → {target_code}",
                    )
                translation = source_language.get_translation(target_language)
                translated = normalize_text(translation.translate(text))
            except TranslationError:
                raise
            except Exception as exc:
                raise TranslationError("service", "Argos 本地翻译失败") from exc
        return TranslationResult(
            request.request_id,
            text,
            translated,
            request.source_language,
            request.target_language,
            request.purpose,
        )


def _detect_source_language(text: str) -> str:
    # Fast local heuristic for the languages most commonly seen in VRChat.
    # Latin text defaults to English; users can select an explicit source for
    # French/German/Spanish/Russian when using Argos.
    if any("\uac00" <= char <= "\ud7af" for char in text):
        return "ko"
    if any("\u3040" <= char <= "\u30ff" for char in text):
        return "ja"
    if any("\u4e00" <= char <= "\u9fff" for char in text):
        return "zh-CN"
    if any("\u0400" <= char <= "\u04ff" for char in text):
        return "ru"
    return "en"
