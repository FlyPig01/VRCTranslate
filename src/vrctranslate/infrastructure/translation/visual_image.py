from __future__ import annotations

from io import BytesIO

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.visual_translation import EncodedVisualFrame, VisualTextRegion


class PillowVisualFrameEncoder:
    def encode(
        self,
        pixels: object,
        *,
        maximum_side: int,
        quality: int,
        regions: tuple[VisualTextRegion, ...] = (),
    ) -> EncodedVisualFrame:
        return encode_visual_frame(
            pixels,
            maximum_side=maximum_side,
            quality=quality,
            regions=regions,
        )


def encode_visual_frame(
    pixels: object,
    *,
    maximum_side: int,
    quality: int,
    regions: tuple[VisualTextRegion, ...] = (),
) -> EncodedVisualFrame:
    """Encode a BGR/BGRA capture in memory and optionally mark region IDs."""

    try:
        array = np.asarray(pixels)
        if array.ndim != 3 or array.shape[2] not in {3, 4}:
            raise ValueError("unsupported pixel shape")
        if array.shape[2] == 4:
            rgb = array[..., [2, 1, 0, 3]]
            image = Image.fromarray(rgb.astype(np.uint8), "RGBA").convert("RGB")
        else:
            rgb = array[..., ::-1]
            image = Image.fromarray(rgb.astype(np.uint8), "RGB")
    except Exception as exc:
        raise TranslationError("image", "无法编码 OCR 捕获画面") from exc

    original_width, original_height = image.size
    limit = max(640, min(4096, int(maximum_side)))
    image.thumbnail((limit, limit), Image.Resampling.LANCZOS)
    scale_x = image.width / max(1, original_width)
    scale_y = image.height / max(1, original_height)
    scaled = tuple(
        VisualTextRegion(
            region.region_id,
            (
                round(region.bbox[0] * scale_x),
                round(region.bbox[1] * scale_y),
                round(region.bbox[2] * scale_x),
                round(region.bbox[3] * scale_y),
            ),
        )
        for region in regions
    )
    if scaled:
        draw = ImageDraw.Draw(image)
        font = ImageFont.load_default()
        for region in scaled:
            left, top, right, bottom = region.bbox
            draw.rectangle((left, top, right, bottom), outline=(255, 48, 48), width=2)
            label = region.region_id
            label_box = draw.textbbox((0, 0), label, font=font)
            label_width = label_box[2] - label_box[0] + 6
            label_height = label_box[3] - label_box[1] + 4
            label_top = max(0, top - label_height)
            draw.rectangle(
                (left, label_top, left + label_width, label_top + label_height),
                fill=(190, 20, 20),
            )
            draw.text((left + 3, label_top + 1), label, fill="white", font=font)

    output = BytesIO()
    image.save(
        output,
        format="JPEG",
        quality=max(40, min(95, int(quality))),
        optimize=True,
    )
    return EncodedVisualFrame(output.getvalue(), "image/jpeg", scaled)
