from __future__ import annotations

import logging
from collections.abc import Callable
from dataclasses import dataclass, replace
from threading import Lock

from vrctranslate.application.dto import (
    GlossarySettings,
    ROMAJI_MODES,
    TranslationProfile,
    TranslationSettings,
)
from vrctranslate.application.ports.glossary_repository import GlossaryRepository
from vrctranslate.application.ports.romaji_converter import RomajiConverter
from vrctranslate.application.ports.translator import Translator
from vrctranslate.application.text_preprocessing.japanese_romaji import (
    preprocess_romaji,
)
from vrctranslate.application.use_cases.glossary import (
    GlossaryProtection,
    match_glossary,
    merge_glossary_entries,
    protect_matches,
)
from vrctranslate.domain.glossary import GlossaryInstruction
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


_LOGGER = logging.getLogger(__name__)


@dataclass(frozen=True, slots=True)
class _PreparedRequest:
    request: TranslationRequest
    fallback: TranslationRequest
    protection: GlossaryProtection
    glossary_mode: str


class TranslateText:
    def __init__(
        self,
        translator: Translator,
        romaji_converter: RomajiConverter | None = None,
        glossary_repository: GlossaryRepository | None = None,
        glossary_settings: Callable[[], GlossarySettings] | None = None,
    ) -> None:
        self._translator = translator
        self._romaji_converter = romaji_converter
        self._glossary_repository = glossary_repository
        self._glossary_settings = glossary_settings or GlossarySettings
        self._glossary_failures: dict[str, int] = {}
        self._glossary_status: dict[str, str] = {}
        self._failure_lock = Lock()

    @property
    def glossary_revision(self) -> int:
        repository = self._glossary_repository
        if repository is None:
            return 0
        settings = self._glossary_settings()
        return (
            repository.revision * 4
            + int(settings.enabled) * 2
            + int(settings.builtin_enabled)
        )

    def glossary_status(self, profile_id: str) -> str:
        with self._failure_lock:
            return self._glossary_status.get(profile_id, "none")

    @staticmethod
    def _route(
        request: TranslationRequest,
        settings: TranslationSettings | TranslationProfile,
    ):
        if not isinstance(settings, TranslationSettings):
            return None
        return settings.ocr_route if request.purpose == "ocr" else settings.self_route

    @classmethod
    def _romaji_mode(
        cls,
        request: TranslationRequest,
        settings: TranslationSettings | TranslationProfile,
    ) -> str:
        route = cls._route(request, settings)
        raw_mode = (
            route.romaji_mode
            if route is not None
            else settings.options.get("_romaji_mode", "auto")
        )
        mode = str(raw_mode)
        return mode if mode in ROMAJI_MODES else "auto"

    @classmethod
    def _route_glossary_enabled(
        cls,
        request: TranslationRequest,
        settings: TranslationSettings | TranslationProfile,
    ) -> bool:
        route = cls._route(request, settings)
        if route is not None:
            return route.glossary_enabled
        return bool(settings.options.get("_glossary_enabled", True))

    def _glossary_mode(self, profile: TranslationProfile) -> str:
        method = getattr(self._translator, "glossary_mode", None)
        if not callable(method):
            return "none"
        mode = str(method(profile))
        if mode not in {"prompt", "local_placeholder"}:
            return "none"
        with self._failure_lock:
            if self._glossary_failures.get(profile.id, 0) >= 3:
                return "none"
        return mode

    @staticmethod
    def _limited_instructions(
        protection: GlossaryProtection,
    ) -> tuple[GlossaryInstruction, ...]:
        output: list[GlossaryInstruction] = []
        total = 0
        for instruction in protection.instructions:
            size = len(instruction.source) + len(instruction.target)
            if len(output) >= 32 or total + size > 4096:
                break
            output.append(instruction)
            total += size
        return tuple(output)

    def _prepare(
        self,
        request: TranslationRequest,
        settings: TranslationSettings | TranslationProfile,
        profile: TranslationProfile,
    ) -> _PreparedRequest:
        repository = self._glossary_repository
        global_settings = self._glossary_settings()
        glossary_mode = self._glossary_mode(profile)
        glossary_active = (
            repository is not None
            and global_settings.enabled
            and self._route_glossary_enabled(request, settings)
            and glossary_mode != "none"
        )

        effective_entries = ()
        protection = GlossaryProtection(request.text)
        if glossary_active and repository is not None:
            user_entries = repository.user_entries()
            effective_entries = merge_glossary_entries(
                repository.builtin_entries(),
                user_entries,
                builtin_enabled=global_settings.builtin_enabled,
            )
            original_matches = match_glossary(
                request.text,
                user_entries,
                request.source_language,
                request.target_language,
                request.purpose,
                user_only=True,
            )
            protection = protect_matches(request.text, original_matches)

        romaji = preprocess_romaji(
            protection.text,
            request.source_language,
            self._romaji_mode(request, settings),
            self._romaji_converter,
        )
        processed_source = (
            "ja"
            if romaji.changed and request.source_language in {"ja", "auto"}
            else request.source_language
        )
        protection = GlossaryProtection(romaji.text, protection.bindings)

        if glossary_active and effective_entries:
            converted_matches = match_glossary(
                protection.text,
                effective_entries,
                processed_source,
                request.target_language,
                request.purpose,
            )
            protection = protect_matches(
                protection.text,
                converted_matches,
                protection,
            )

        fallback = replace(
            request,
            text=protection.restore_sources(),
            source_language=processed_source,
            glossary=(),
        )
        if glossary_mode == "prompt" and protection.bindings:
            translated_request = replace(
                fallback,
                glossary=self._limited_instructions(protection),
            )
        elif glossary_mode == "local_placeholder" and protection.bindings:
            translated_request = replace(
                fallback,
                text=protection.text,
            )
        else:
            translated_request = fallback
        return _PreparedRequest(
            translated_request,
            fallback,
            protection,
            glossary_mode,
        )

    @staticmethod
    def _restore_original(
        result: TranslationResult,
        original_request: TranslationRequest,
    ) -> TranslationResult:
        if result.original == original_request.text:
            return result
        return replace(result, original=original_request.text)

    def _finish(
        self,
        result: TranslationResult,
        prepared: _PreparedRequest,
        original: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult | None:
        if prepared.glossary_mode != "local_placeholder" or not prepared.protection.bindings:
            if prepared.glossary_mode == "prompt" and prepared.protection.bindings:
                with self._failure_lock:
                    self._glossary_status[profile.id] = "prompt"
            return self._restore_original(result, original)
        restored = prepared.protection.restore_targets(result.translated)
        if restored is None:
            self._record_placeholder_failure(profile.id)
            return None
        with self._failure_lock:
            self._glossary_status[profile.id] = "compatible"
        return self._restore_original(replace(result, translated=restored), original)

    def _record_placeholder_failure(self, profile_id: str) -> None:
        with self._failure_lock:
            count = self._glossary_failures.get(profile_id, 0) + 1
            self._glossary_failures[profile_id] = count
            self._glossary_status[profile_id] = "fallback"
        _LOGGER.warning(
            "glossary_placeholder_failed profile_id=%s count=%d",
            profile_id,
            count,
        )

    def execute(
        self,
        request: TranslationRequest,
        settings: TranslationSettings | TranslationProfile,
    ) -> TranslationResult:
        profile = (
            settings.profile_for_purpose(request.purpose)
            if isinstance(settings, TranslationSettings)
            else settings
        )
        prepared = self._prepare(request, settings, profile)
        result = self._translator.translate(prepared.request, profile)
        finished = self._finish(result, prepared, request, profile)
        if finished is not None:
            return finished
        fallback = self._translator.translate(prepared.fallback, profile)
        return self._restore_original(fallback, request)

    def execute_batch(
        self,
        requests: list[TranslationRequest],
        settings: TranslationSettings | TranslationProfile,
    ) -> list[TranslationResult]:
        if not requests:
            return []
        profile = (
            settings.profile_for_purpose(requests[0].purpose)
            if isinstance(settings, TranslationSettings)
            else settings
        )
        prepared = [self._prepare(request, settings, profile) for request in requests]
        method = getattr(self._translator, "translate_batch", None)
        if callable(method):
            results = list(method([item.request for item in prepared], profile))
        else:
            results = [
                self._translator.translate(item.request, profile) for item in prepared
            ]
        if len(results) != len(requests):
            raise RuntimeError("batch translation count mismatch")
        output: list[TranslationResult] = []
        for result, item, original in zip(results, prepared, requests, strict=True):
            finished = self._finish(result, item, original, profile)
            if finished is None:
                fallback = self._translator.translate(item.fallback, profile)
                finished = self._restore_original(fallback, original)
            output.append(finished)
        return output
