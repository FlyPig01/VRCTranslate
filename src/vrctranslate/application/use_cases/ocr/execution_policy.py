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
        # All providers run with the same concurrency policy. Slow LLM-backed
        # endpoints (e.g. DeepSeek via openai_compatible) previously forced
        # max_workers=1 with a capacity of 2, which serialised every request
        # and stalled the OCR pipeline whenever a single translation was slow.
        # Two workers let concurrent requests overlap, and the bounded queue
        # keeps a small buffer so bursts of OCR frames are not dropped while
        # the workers are busy.
        return cls(
            max_workers=2,
            queue_capacity=min(route.queue_limit, 4),
            # Treat one OCR frame as one scheduling unit. Adapters without a
            # native batch endpoint fall back to ordered calls in one worker
            # instead of silently dropping blocks beyond the queue capacity.
            batch_enabled=True,
        )
