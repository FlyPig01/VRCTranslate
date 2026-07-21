from __future__ import annotations

import argparse
import csv
import time
from pathlib import Path
from typing import Any
from uuid import uuid4

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from sacrebleu.metrics import BLEU, CHRF, TER

from benchmarks.benchmark_config import (
    END_TO_END_SAMPLE_COUNT,
    LANGUAGE_BY_CODE,
    LANGUAGES,
    MODELS_ROOT,
    RESULTS_ROOT,
    SPOKEN_LANGUAGES,
    ensure_directories,
)
from benchmarks.common import (
    aligned_sample_indices,
    character_error_rate,
    flores_lines,
    mean,
    normalized_text,
    percentile,
    read_json,
    write_csv,
    write_json,
)
from benchmarks.run_translation_benchmark import (
    _minimum_interval,
    _profiles,
    _translate_with_retry,
)
from vrctranslate.application.use_cases.ocr.text_composer import compose_ocr_texts
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager
from vrctranslate.infrastructure.ocr.rapidocr_engine import RapidOcrEngine


FONT_ROOT = Path("C:/Windows/Fonts")


def _profile_routes() -> tuple[dict[str, tuple[Any, Any]], dict[str, str]]:
    profile_items, _availability = _profiles()
    profiles = {key: (profile, adapter) for key, profile, adapter in profile_items}
    summaries = read_json(RESULTS_ROOT / "translation_summary.json")
    routes: dict[str, str] = {}
    for source in (item.code for item in LANGUAGES):
        target = "en" if source == "zh-CN" else "zh-CN"
        candidates = [
            item
            for item in summaries
            if item["source_language"] == source
            and item["target_language"] == target
            and item["success_rate"] >= 0.9
            and item["chrf"] is not None
            and item["profile_key"] in profiles
        ]
        if not candidates:
            raise RuntimeError(f"no successful translation route for {source}->{target}")
        best = max(
            candidates,
            key=lambda item: (
                float(item["chrf"]),
                -float(item["latency_p50_ms"]),
            ),
        )
        routes[source] = str(best["profile_key"])
    return profiles, routes


def _font(language: str, size: int = 32) -> ImageFont.FreeTypeFont:
    for name in LANGUAGE_BY_CODE[language].font_candidates:
        path = FONT_ROOT / name
        if path.is_file():
            return ImageFont.truetype(str(path), size)
    raise FileNotFoundError(language)


def _wrap(text: str, font: ImageFont.FreeTypeFont, cjk: bool) -> list[str]:
    max_width = 920
    tokens = list(text) if cjk else text.split()
    lines: list[str] = []
    current = ""
    probe = Image.new("RGB", (1, 1))
    draw = ImageDraw.Draw(probe)
    for token in tokens:
        candidate = current + token if cjk else f"{current} {token}".strip()
        width = draw.textbbox((0, 0), candidate, font=font, stroke_width=1)[2]
        if current and width > max_width:
            lines.append(current)
            current = token
        else:
            current = candidate
    if current:
        lines.append(current)
    return lines[:6]


def _render(text: str, language: str) -> np.ndarray:
    font = _font(language)
    lines = _wrap(text, font, LANGUAGE_BY_CODE[language].cjk_metric)
    line_height = 44
    image = Image.new("RGB", (980, 28 + line_height * len(lines)), (18, 27, 41))
    draw = ImageDraw.Draw(image)
    for index, line in enumerate(lines):
        draw.text(
            (24, 12 + line_height * index),
            line,
            font=font,
            fill=(246, 249, 252),
            stroke_width=1,
            stroke_fill=(0, 0, 0),
        )
    return np.asarray(image)


def _translate(
    profile_key: str,
    profiles: dict[str, tuple[Any, Any]],
    source: str,
    target: str,
    text: str,
) -> tuple[str, float]:
    profile, adapter = profiles[profile_key]
    started = time.perf_counter()
    hypothesis, _retries = _translate_with_retry(
        adapter,
        TranslationRequest(uuid4().hex, text, source, target, "ocr"),
        profile,
    )
    elapsed_ms = (time.perf_counter() - started) * 1000
    remaining = _minimum_interval(profile.provider) - elapsed_ms / 1000.0
    if remaining > 0:
        time.sleep(remaining)
    return hypothesis, elapsed_ms


def run_ocr_end_to_end(
    profiles: dict[str, tuple[Any, Any]],
    routes: dict[str, str],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    manager = OcrModelManager(
        MODELS_ROOT / "ocr",
        MODELS_ROOT.parent / "cache" / "ocr-models",
    )
    population = min(len(flores_lines(item.code)) for item in LANGUAGES)
    indices = aligned_sample_indices(END_TO_END_SAMPLE_COUNT, population)
    chrf = CHRF(word_order=2)
    cases: list[dict[str, Any]] = []
    summaries: list[dict[str, Any]] = []
    for spec in LANGUAGES:
        target = "en" if spec.code == "zh-CN" else "zh-CN"
        engine = RapidOcrEngine(manager, spec.code)
        language_cases: list[dict[str, Any]] = []
        for index in indices:
            source = flores_lines(spec.code)[index]
            reference = flores_lines(target)[index]
            started = time.perf_counter()
            raw = engine.recognize(_render(source, spec.code))
            composed = compose_ocr_texts(raw)
            recognized = " ".join(item.text for item in composed)
            ocr_ms = (time.perf_counter() - started) * 1000
            try:
                translated, translation_ms = _translate(
                    routes[spec.code],
                    profiles,
                    spec.code,
                    target,
                    recognized,
                )
                status = "ok"
                error = ""
            except Exception as exc:
                translated = ""
                translation_ms = 0.0
                status = "failed"
                error = str(getattr(exc, "user_message", type(exc).__name__))[:200]
            row = {
                "pipeline": "ocr_translation",
                "language": spec.code,
                "target_language": target,
                "profile_key": routes[spec.code],
                "sample_index": index,
                "source": source,
                "recognized": recognized,
                "reference": reference,
                "translated": translated,
                "ocr_cer": character_error_rate(source, recognized),
                "translation_chrf": (
                    chrf.sentence_score(translated, [reference]).score
                    if translated
                    else 0.0
                ),
                "ocr_latency_ms": ocr_ms,
                "translation_latency_ms": translation_ms,
                "total_latency_ms": ocr_ms + translation_ms,
                "status": status,
                "error": error,
            }
            cases.append(row)
            language_cases.append(row)
        successful = [row for row in language_cases if row["status"] == "ok"]
        summaries.append(
            {
                "pipeline": "ocr_translation",
                "language": spec.code,
                "target_language": target,
                "profile_key": routes[spec.code],
                "cases": len(language_cases),
                "success_rate": len(successful) / len(language_cases),
                "ocr_cer": mean(float(row["ocr_cer"]) for row in language_cases),
                "translation_chrf": mean(
                    float(row["translation_chrf"]) for row in successful
                ),
                "total_latency_p50_ms": percentile(
                    (float(row["total_latency_ms"]) for row in successful), 50
                ),
                "total_latency_p95_ms": percentile(
                    (float(row["total_latency_ms"]) for row in successful), 95
                ),
            }
        )
        print(f"[e2e] OCR {spec.code}: complete", flush=True)
    return cases, summaries


def _best_asr_routes() -> dict[str, str]:
    summaries = read_json(RESULTS_ROOT / "asr_summary.json")
    routes: dict[str, str] = {}
    for spec in SPOKEN_LANGUAGES:
        candidates = [
            item
            for item in summaries
            if item["language"] == spec.code and item["success_rate"] >= 0.9
        ]
        if not candidates:
            raise RuntimeError(f"no ASR route for {spec.code}")
        best_error = min(float(item["primary_error_rate"]) for item in candidates)
        quality_equivalent = [
            item
            for item in candidates
            if float(item["primary_error_rate"]) <= best_error + 0.02
        ]
        routes[spec.code] = str(
            min(quality_equivalent, key=lambda item: float(item["rtf"]))["engine"]
        )
    return routes


def _read_asr_cases() -> list[dict[str, str]]:
    with (RESULTS_ROOT / "asr_cases.csv").open(
        "r", encoding="utf-8-sig", newline=""
    ) as stream:
        return list(csv.DictReader(stream))


def run_asr_end_to_end(
    profiles: dict[str, tuple[Any, Any]],
    translation_routes: dict[str, str],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], dict[str, str]]:
    asr_routes = _best_asr_routes()
    source_cases = _read_asr_cases()
    chrf = CHRF(word_order=2)
    cases: list[dict[str, Any]] = []
    summaries: list[dict[str, Any]] = []
    for spec in SPOKEN_LANGUAGES:
        selected = [
            row
            for row in source_cases
            if row["language"] == spec.code
            and row["engine"] == asr_routes[spec.code]
            and row["status"] == "ok"
        ][:END_TO_END_SAMPLE_COUNT]
        language_cases: list[dict[str, Any]] = []
        for source in selected:
            target = source["translation_target"]
            try:
                translated, translation_ms = _translate(
                    translation_routes[spec.code],
                    profiles,
                    spec.code,
                    target,
                    source["hypothesis"],
                )
                status = "ok"
                error = ""
            except Exception as exc:
                translated = ""
                translation_ms = 0.0
                status = "failed"
                error = str(getattr(exc, "user_message", type(exc).__name__))[:200]
            asr_ms = float(source["latency_ms"])
            reference = source["translation_reference"]
            row = {
                "pipeline": "asr_translation",
                "language": spec.code,
                "target_language": target,
                "asr_engine": asr_routes[spec.code],
                "profile_key": translation_routes[spec.code],
                "filename": source["filename"],
                "source": source["reference"],
                "recognized": source["hypothesis"],
                "reference": reference,
                "translated": translated,
                "asr_cer": float(source["cer"]),
                "asr_wer": float(source["wer"]),
                "translation_chrf": (
                    chrf.sentence_score(translated, [reference]).score
                    if translated
                    else 0.0
                ),
                "asr_latency_ms": asr_ms,
                "translation_latency_ms": translation_ms,
                "total_latency_ms": asr_ms + translation_ms,
                "status": status,
                "error": error,
            }
            cases.append(row)
            language_cases.append(row)
        successful = [row for row in language_cases if row["status"] == "ok"]
        summaries.append(
            {
                "pipeline": "asr_translation",
                "language": spec.code,
                "target_language": (
                    successful[0]["target_language"] if successful else ""
                ),
                "asr_engine": asr_routes[spec.code],
                "profile_key": translation_routes[spec.code],
                "cases": len(language_cases),
                "success_rate": len(successful) / len(language_cases),
                "asr_primary_error_rate": mean(
                    float(row["asr_cer"] if spec.cjk_metric else row["asr_wer"])
                    for row in language_cases
                ),
                "translation_chrf": mean(
                    float(row["translation_chrf"]) for row in successful
                ),
                "total_latency_p50_ms": percentile(
                    (float(row["total_latency_ms"]) for row in successful), 50
                ),
                "total_latency_p95_ms": percentile(
                    (float(row["total_latency_ms"]) for row in successful), 95
                ),
            }
        )
        print(f"[e2e] ASR {spec.code}: complete", flush=True)
    return cases, summaries, asr_routes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--asr-only", action="store_true")
    args = parser.parse_args()
    ensure_directories()
    profiles, translation_routes = _profile_routes()
    if args.asr_only:
        asr_cases, asr_summaries, asr_routes = run_asr_end_to_end(
            profiles, translation_routes
        )
        write_csv(RESULTS_ROOT / "end_to_end_asr_cases.csv", asr_cases)
        existing = read_json(RESULTS_ROOT / "end_to_end_summary.json")
        write_json(
            RESULTS_ROOT / "end_to_end_summary.json",
            [
                *[item for item in existing if item["pipeline"] != "asr_translation"],
                *asr_summaries,
            ],
        )
        write_json(
            RESULTS_ROOT / "selected_routes.json",
            {
                "translation": translation_routes,
                "asr": asr_routes,
                "selection_rule": (
                    "Translation: highest FLORES chrF with >=90% success, then lowest "
                    "latency. ASR: among engines within 2 percentage points of the "
                    "lowest primary error rate, choose the lowest RTF."
                ),
            },
        )
        return 0
    ocr_cases, ocr_summaries = run_ocr_end_to_end(profiles, translation_routes)
    asr_cases, asr_summaries, asr_routes = run_asr_end_to_end(
        profiles, translation_routes
    )
    write_csv(RESULTS_ROOT / "end_to_end_ocr_cases.csv", ocr_cases)
    write_csv(RESULTS_ROOT / "end_to_end_asr_cases.csv", asr_cases)
    write_json(
        RESULTS_ROOT / "end_to_end_summary.json",
        [*ocr_summaries, *asr_summaries],
    )
    write_json(
        RESULTS_ROOT / "selected_routes.json",
        {
            "translation": translation_routes,
            "asr": asr_routes,
            "selection_rule": (
                "Translation: highest FLORES chrF with >=90% success, then lowest "
                "latency. ASR: among engines within 2 percentage points of the "
                "lowest primary error rate, choose the lowest RTF."
            ),
        },
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
