from __future__ import annotations

from typing import Protocol

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.visual_translation import (
    VisualTranslationRequest,
    VisualTranslationResult,
)


class VisualTranslator(Protocol):
    def translate(
        self,
        request: VisualTranslationRequest,
        profile: TranslationProfile,
    ) -> VisualTranslationResult: ...
