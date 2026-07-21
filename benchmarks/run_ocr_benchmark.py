from __future__ import annotations

import gc
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from benchmarks.benchmark_config import (
    LANGUAGE_BY_CODE,
    LANGUAGES,
    MODELS_ROOT,
    OCR_SAMPLE_COUNT,
    RESULTS_ROOT,
    ensure_directories,
)
from benchmarks.common import (
    ResourceMonitor,
    character_error_rate,
    dataclass_dict,
    flores_lines,
    mean,
    normalized_text,
    percentile,
    word_error_rate,
    write_csv,
    write_json,
)
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager
from vrctranslate.infrastructure.ocr.rapidocr_engine import RapidOcrEngine
from vrctranslate.application.use_cases.ocr.text_composer import compose_ocr_texts


FONT_ROOT = Path("C:/Windows/Fonts")
FONT_SIZES = (16, 24, 32, 40)
BACKGROUNDS = ("dark", "light")
STROKE_WIDTHS = (0, 1)


def _font_path(language: str) -> Path:
    for candidate in LANGUAGE_BY_CODE[language].font_candidates:
        path = FONT_ROOT / candidate
        if path.is_file():
            return path
    raise FileNotFoundError(f"no test font for {language}")


def _clip_text(value: str, language: str) -> str:
    text = " ".join(value.split())
    spec = LANGUAGE_BY_CODE[language]
    limit = 26 if spec.cjk_metric else 52
    if len(text) <= limit:
        return text
    clipped = text[:limit]
    if not spec.cjk_metric and " " in clipped:
        clipped = clipped.rsplit(" ", 1)[0]
    return clipped.rstrip(" ,.;:!?，。；：！？")


def _render(
    text: str,
    font_path: Path,
    font_size: int,
    background: str,
    stroke_width: int,
) -> np.ndarray:
    font = ImageFont.truetype(str(font_path), font_size)
    probe = Image.new("RGB", (1, 1))
    bounds = ImageDraw.Draw(probe).textbbox(
        (0, 0),
        text,
        font=font,
        stroke_width=stroke_width,
    )
    width = max(120, bounds[2] - bounds[0] + 32)
    height = max(52, bounds[3] - bounds[1] + 30)
    if background == "dark":
        bg = (20, 29, 43)
        fill = (246, 249, 252)
        stroke_fill = (0, 0, 0)
    else:
        bg = (245, 247, 250)
        fill = (25, 35, 49)
        stroke_fill = (255, 255, 255)
    image = Image.new("RGB", (width, height), bg)
    ImageDraw.Draw(image).text(
        (16 - bounds[0], 15 - bounds[1]),
        text,
        font=font,
        fill=fill,
        stroke_width=stroke_width,
        stroke_fill=stroke_fill,
    )
    return np.asarray(image)


def _compose(items: list[object]) -> tuple[str, list[object]]:
    composed = compose_ocr_texts(items)
    text = " ".join(
        str(item.text).strip() for item in composed if str(item.text).strip()
    )
    return text, composed


def main() -> int:
    ensure_directories()
    manager = OcrModelManager(
        MODELS_ROOT / "ocr",
        MODELS_ROOT.parent / "cache" / "ocr-models",
    )
    cases: list[dict[str, object]] = []
    summaries: list[dict[str, object]] = []
    for spec in LANGUAGES:
        if not manager.status(spec.ocr_package).installed:
            raise FileNotFoundError(f"OCR package not installed: {spec.ocr_package}")
        texts = [
            _clip_text(line, spec.code)
            for line in flores_lines(spec.code)[:OCR_SAMPLE_COUNT]
        ]
        font_path = _font_path(spec.code)
        language_cases: list[dict[str, object]] = []
        cold_start_ms = 0.0
        with ResourceMonitor() as monitor:
            engine = RapidOcrEngine(manager, spec.code)
            first = True
            for sample_index, expected in enumerate(texts):
                for font_size in FONT_SIZES:
                    for background in BACKGROUNDS:
                        for stroke_width in STROKE_WIDTHS:
                            pixels = _render(
                                expected,
                                font_path,
                                font_size,
                                background,
                                stroke_width,
                            )
                            started = time.perf_counter()
                            items = engine.recognize(pixels)
                            elapsed_ms = (time.perf_counter() - started) * 1000
                            if first:
                                cold_start_ms = elapsed_ms
                                first = False
                            actual, composed = _compose(items)
                            confidence = mean(
                                float(item.confidence) for item in composed
                            )
                            row: dict[str, object] = {
                                "language": spec.code,
                                "language_name": spec.display_name,
                                "ocr_package": spec.ocr_package,
                                "sample": sample_index,
                                "font_size": font_size,
                                "background": background,
                                "stroke_width": stroke_width,
                                "expected": expected,
                                "actual": actual,
                                "cer": character_error_rate(expected, actual),
                                "wer": word_error_rate(expected, actual),
                                "exact": normalized_text(expected)
                                == normalized_text(actual),
                                "confidence": confidence,
                                "latency_ms": elapsed_ms,
                                "detected_regions": len(items),
                                "composed_regions": len(composed),
                            }
                            cases.append(row)
                            language_cases.append(row)
            del engine
            gc.collect()
        resources = monitor.result()
        status = manager.status(spec.ocr_package)
        summary: dict[str, object] = {
            "language": spec.code,
            "language_name": spec.display_name,
            "ocr_package": spec.ocr_package,
            "cases": len(language_cases),
            "cer": mean(float(row["cer"]) for row in language_cases),
            "wer": (
                None
                if spec.cjk_metric
                else mean(float(row["wer"]) for row in language_cases)
            ),
            "exact_rate": mean(1.0 if row["exact"] else 0.0 for row in language_cases),
            "average_confidence": mean(
                float(row["confidence"]) for row in language_cases
            ),
            "cold_start_ms": cold_start_ms,
            "latency_p50_ms": percentile(
                (float(row["latency_ms"]) for row in language_cases), 50
            ),
            "latency_p95_ms": percentile(
                (float(row["latency_ms"]) for row in language_cases), 95
            ),
            "model_exclusive_mib": status.exclusive_size / 2**20,
            "resources": dataclass_dict(resources),
        }
        summaries.append(summary)
        print(
            f"[ocr] {spec.code}: CER={float(summary['cer']):.3f}, "
            f"exact={float(summary['exact_rate']):.1%}, "
            f"p95={float(summary['latency_p95_ms']):.1f}ms"
        )
    write_csv(RESULTS_ROOT / "ocr_cases.csv", cases)
    write_json(RESULTS_ROOT / "ocr_summary.json", summaries)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
