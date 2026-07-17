from __future__ import annotations

from dataclasses import dataclass

from vrctranslate.application.dto import (
    TranslationProfile,
    TranslationRouteSettings,
)


@dataclass(frozen=True, slots=True)
class OcrExecutionPolicy:
    """Provider-aware concurrency, queue and batching decisions."""

    max_workers: int
    queue_capacity: int
    batch_enabled: bool

    @classmethod
    def create(
        cls,
        profile: TranslationProfile,
        route: TranslationRouteSettings,
    ) -> OcrExecutionPolicy:
        serialized_provider = profile.provider == "openai_compatible"
        capacity = (
            min(route.queue_limit, 2)
            if profile.provider == "openai_compatible"
            else route.queue_limit
        )
        return cls(
            max_workers=1 if serialized_provider else 2,
            queue_capacity=capacity,
            batch_enabled=profile.provider in {"test", "deepl", "google_cloud"},
        )
