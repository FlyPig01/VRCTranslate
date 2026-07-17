from __future__ import annotations

from collections.abc import Iterable

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.ports.translator import (
    TranslationAdapter,
    TranslationCapabilities,
)
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


class TranslationRouter:
    def __init__(self, adapters: Iterable[TranslationAdapter]) -> None:
        self._adapters: dict[str, TranslationAdapter] = {}
        for adapter in adapters:
            capabilities = adapter.capabilities()
            self._adapters[capabilities.provider] = adapter

    def capabilities(self) -> list[TranslationCapabilities]:
        return [adapter.capabilities() for adapter in self._adapters.values()]

    def has_provider(self, provider: str) -> bool:
        return provider in self._adapters

    def translate(
        self,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult:
        adapter = self._adapters.get(profile.provider)
        if adapter is None:
            raise TranslationError(
                "configuration",
                f"未知翻译服务类型：{profile.provider}",
            )
        return adapter.translate(request, profile)

    def translate_batch(
        self,
        requests: list[TranslationRequest],
        profile: TranslationProfile,
    ) -> list[TranslationResult]:
        if not requests:
            return []
        adapter = self._adapters.get(profile.provider)
        if adapter is None:
            raise TranslationError(
                "configuration",
                f"未知翻译服务类型：{profile.provider}",
            )
        method = getattr(adapter, "translate_batch", None)
        if callable(method):
            return list(method(requests, profile))
        return [adapter.translate(request, profile) for request in requests]
