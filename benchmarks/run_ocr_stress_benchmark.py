from __future__ import annotations

import time

import numpy as np
from PIL import Image, ImageFilter

from benchmarks.benchmark_config import (
    LANGUAGES,
    MODELS_ROOT,
    OCR_SAMPLE_COUNT,
    RESULTS_ROOT,
    ensure_directories,
)
from benchmarks.common import (
    character_error_rate,
    flores_lines,
    mean,
    normalized_text,
    percentile,
    write_csv,
    write_json,
)
from benchmarks.run_ocr_benchmark import _clip_text, _font_path, _render
from vrctranslate.application.use_cases.ocr.text_composer import compose_ocr_texts
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager
from vrctranslate.infrastructure.ocr.rapidocr_engine import RapidOcrEngine


CONDITIONS = ("baseline", "low_contrast", "gaussian_blur", "tight_crop")


def _condition(pixels: np.ndarray, condition: str) -> np.ndarray:
    image = Image.fromarray(pixels)
    if condition == "low_contrast":
        neutral = Image.new("RGB", image.size, (92, 98, 108))
        image = Image.blend(image, neutral, 0.62)
    elif condition == "gaussian_blur":
        image = image.filter(ImageFilter.GaussianBlur(1.15))
    elif condition == "tight_crop":
        image = image.crop((14, 0, max(15, image.width - 14), image.height))
    return np.asarray(image)


def main() -> int:
    ensure_directories()
    manager = OcrModelManager(
        MODELS_ROOT / "ocr",
        MODELS_ROOT.parent / "cache" / "ocr-models",
    )
    cases: list[dict[str, object]] = []
    summaries: list[dict[str, object]] = []
    for spec in LANGUAGES:
        engine = RapidOcrEngine(manager, spec.code)
        texts = [
            _clip_text(line, spec.code)
            for line in flores_lines(spec.code)[:OCR_SAMPLE_COUNT]
        ]
        for sample, expected in enumerate(texts):
            baseline = _render(expected, _font_path(spec.code), 24, "dark", 1)
            for condition in CONDITIONS:
                started = time.perf_counter()
                raw = engine.recognize(_condition(baseline, condition))
                composed = compose_ocr_texts(raw)
                actual = " ".join(item.text for item in composed)
                cases.append(
                    {
                        "language": spec.code,
                        "condition": condition,
                        "sample": sample,
                        "expected": expected,
                        "actual": actual,
                        "cer": character_error_rate(expected, actual),
                        "exact": normalized_text(expected) == normalized_text(actual),
                        "latency_ms": (time.perf_counter() - started) * 1000,
                    }
                )
        for condition in CONDITIONS:
            subset = [
                row
                for row in cases
                if row["language"] == spec.code and row["condition"] == condition
            ]
            summaries.append(
                {
                    "language": spec.code,
                    "language_name": spec.display_name,
                    "condition": condition,
                    "cases": len(subset),
                    "cer": mean(float(row["cer"]) for row in subset),
                    "exact_rate": mean(
                        1.0 if row["exact"] else 0.0 for row in subset
                    ),
                    "latency_p50_ms": percentile(
                        (float(row["latency_ms"]) for row in subset), 50
                    ),
                }
            )
        print(f"[ocr-stress] {spec.code}: complete", flush=True)
    write_csv(RESULTS_ROOT / "ocr_stress_cases.csv", cases)
    write_json(RESULTS_ROOT / "ocr_stress_summary.json", summaries)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
