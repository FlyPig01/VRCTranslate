from __future__ import annotations

from dataclasses import replace
from typing import Callable

from vrctranslate.application.dto import GlossarySettings, TranslationProfile
from vrctranslate.application.ports.glossary_repository import GlossaryRepository
from vrctranslate.application.ports.visual_translator import VisualTranslator
from vrctranslate.application.use_cases.glossary.merge import merge_glossary_entries
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.glossary import GlossaryInstruction
from vrctranslate.domain.visual_translation import (
    VisualTranslationRequest,
    VisualTranslationResult,
)


class TranslateVisualFrame:
    """Validate a visual profile and attach a bounded, user-first glossary."""

    def __init__(
        self,
        translator: VisualTranslator,
        glossary_repository: GlossaryRepository | None = None,
        glossary_settings: Callable[[], GlossarySettings] | None = None,
    ) -> None:
        self._translator = translator
        self._repository = glossary_repository
        self._glossary_settings = glossary_settings

    def execute(
        self,
        request: VisualTranslationRequest,
        profile: TranslationProfile,
        *,
        glossary_enabled: bool = True,
    ) -> VisualTranslationResult:
        if profile.provider != "multimodal_openai":
            raise TranslationError("configuration", "当前 OCR 档案不支持图片翻译")
        glossary = self._visual_glossary(request, glossary_enabled)
        return self._translator.translate(replace(request, glossary=glossary), profile)

    def _visual_glossary(
        self,
        request: VisualTranslationRequest,
        enabled: bool,
    ) -> tuple[GlossaryInstruction, ...]:
        repository = self._repository
        if not enabled or repository is None:
            return ()
        settings = (
            self._glossary_settings()
            if self._glossary_settings is not None
            else GlossarySettings()
        )
        if not settings.enabled:
            return ()
        entries = merge_glossary_entries(
            repository.builtin_entries(),
            repository.user_entries(),
            builtin_enabled=settings.builtin_enabled,
        )
        eligible = [
            entry
            for entry in entries
            if entry.target_language in {"any", request.target_language}
            and (
                request.source_language == "auto"
                or entry.source_language in {"any", request.source_language}
            )
        ]
        eligible.sort(key=lambda entry: (entry.builtin, entry.id))
        seen: set[tuple[str, str]] = set()
        output: list[GlossaryInstruction] = []
        for entry in eligible:
            key = (entry.source.casefold(), entry.target.casefold())
            if key in seen:
                continue
            seen.add(key)
            output.append(GlossaryInstruction(entry.source, entry.target))
            if len(output) >= 128:
                break
        return tuple(output)
