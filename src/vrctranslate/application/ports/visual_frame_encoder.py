from __future__ import annotations

from typing import Protocol

from vrctranslate.domain.visual_translation import EncodedVisualFrame, VisualTextRegion


class VisualFrameEncoder(Protocol):
    def encode(
        self,
        pixels: object,
        *,
        maximum_side: int,
        quality: int,
        regions: tuple[VisualTextRegion, ...] = (),
    ) -> EncodedVisualFrame: ...
