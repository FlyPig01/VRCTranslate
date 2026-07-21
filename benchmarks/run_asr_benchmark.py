from __future__ import annotations

import argparse
import csv
import gc
import json
import os
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path
from typing import Any
from uuid import uuid4

import httpx
import numpy as np
import psutil

from benchmarks.benchmark_config import (
    FLEURS_ROOT,
    MODELS_ROOT,
    RESULTS_ROOT,
    SENSEVOICE_LANGUAGES,
    SPOKEN_LANGUAGES,
    TMP_ROOT,
    TOOLS_ROOT,
    ensure_directories,
)
from benchmarks.common import (
    ResourceMonitor,
    character_error_rate,
    dataclass_dict,
    mean,
    normalized_text,
    percentile,
    read_json,
    word_error_rate,
    write_csv,
    write_json,
)
from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.domain.speech import SpeechRecognitionRequest
from vrctranslate.infrastructure.speech.local_component_manager import (
    SenseVoiceComponentManager,
)
from vrctranslate.infrastructure.speech.sensevoice_local import (
    SenseVoiceLocalSpeechRecognizer,
)


WHISPER_MODEL = MODELS_ROOT / "whisper.cpp" / "ggml-base-q5_1.bin"
WHISPER_SMALL_MODEL = MODELS_ROOT / "whisper.cpp" / "ggml-small-q5_1.bin"
WHISPER_SERVER = next(TOOLS_ROOT.rglob("whisper-server.exe"), None)


def _read_wave(path: Path) -> tuple[np.ndarray, int]:
    content = path.read_bytes()
    if content[:4] != b"RIFF" or content[8:12] != b"WAVE":
        raise ValueError(f"unsupported audio container: {path.name}")
    position = 12
    audio_format = channels = sample_rate = bits = 0
    payload = b""
    while position + 8 <= len(content):
        chunk_id = content[position : position + 4]
        chunk_size = struct.unpack_from("<I", content, position + 4)[0]
        start = position + 8
        chunk = content[start : start + chunk_size]
        if chunk_id == b"fmt ":
            audio_format, channels, sample_rate = struct.unpack_from("<HHI", chunk, 0)
            bits = struct.unpack_from("<H", chunk, 14)[0]
        elif chunk_id == b"data":
            payload = chunk
        position = start + chunk_size + chunk_size % 2
    if not payload or channels < 1 or sample_rate < 1:
        raise ValueError(f"invalid WAVE file: {path.name}")
    if audio_format == 3 and bits == 32:
        samples = np.frombuffer(payload, dtype="<f4").astype(np.float32)
    elif audio_format == 1 and bits == 16:
        samples = np.frombuffer(payload, dtype="<i2").astype(np.float32) / 32768.0
    else:
        raise ValueError(f"unsupported WAVE encoding {audio_format}/{bits}")
    if channels > 1:
        samples = samples.reshape(-1, channels).mean(axis=1)
    return np.ascontiguousarray(samples), sample_rate


def _pcm16(samples: np.ndarray) -> bytes:
    return (np.clip(samples, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()


def _manifest(spec: Any) -> list[dict[str, Any]]:
    assert spec.fleurs_code is not None
    return read_json(FLEURS_ROOT / spec.fleurs_code / "manifest.json")


def _audio_path(spec: Any, item: dict[str, Any]) -> Path:
    assert spec.fleurs_code is not None
    return FLEURS_ROOT / spec.fleurs_code / "audio" / item["filename"]


def _case_row(
    engine: str,
    spec: Any,
    item: dict[str, Any],
    hypothesis: str,
    elapsed_ms: float,
    detected_language: str = "",
    status: str = "ok",
    error: str = "",
) -> dict[str, Any]:
    reference = str(item["raw_transcription"])
    return {
        "engine": engine,
        "language": spec.code,
        "language_name": spec.display_name,
        "filename": item["filename"],
        "reference": reference,
        "hypothesis": hypothesis,
        "duration_seconds": float(item["duration_seconds"]),
        "latency_ms": elapsed_ms,
        "rtf": elapsed_ms / 1000.0 / float(item["duration_seconds"]),
        "cer": character_error_rate(reference, hypothesis),
        "wer": word_error_rate(reference, hypothesis),
        "exact": normalized_text(reference) == normalized_text(hypothesis),
        "detected_language": detected_language,
        "language_correct": detected_language in {"", spec.code},
        "status": status,
        "error": error,
        "translation_target": item["translation_target"],
        "translation_reference": item["translation_reference"],
        "flores_split": item["flores_split"],
        "flores_index": item["flores_index"],
    }


def _summary(
    engine: str,
    spec: Any,
    rows: list[dict[str, Any]],
    cold_start_ms: float,
    resources: Any,
    model_size: int,
) -> dict[str, Any]:
    successful = [row for row in rows if row["status"] == "ok"]
    primary = "cer" if spec.cjk_metric else "wer"
    return {
        "engine": engine,
        "language": spec.code,
        "language_name": spec.display_name,
        "cases": len(rows),
        "succeeded": len(successful),
        "success_rate": len(successful) / len(rows) if rows else 0.0,
        "primary_metric": primary,
        "primary_error_rate": mean(float(row[primary]) for row in successful),
        "cer": mean(float(row["cer"]) for row in successful),
        "wer": mean(float(row["wer"]) for row in successful),
        "exact_rate": mean(1.0 if row["exact"] else 0.0 for row in successful),
        "language_accuracy": mean(
            1.0 if row["language_correct"] else 0.0 for row in successful
        ),
        "cold_start_ms": cold_start_ms,
        "latency_p50_ms": percentile(
            (float(row["latency_ms"]) for row in successful), 50
        ),
        "latency_p95_ms": percentile(
            (float(row["latency_ms"]) for row in successful), 95
        ),
        "rtf": mean(float(row["rtf"]) for row in successful),
        "audio_seconds": sum(float(row["duration_seconds"]) for row in rows),
        "model_and_runtime_mib": model_size / 2**20,
        "resources": dataclass_dict(resources),
    }


def _sensevoice_paths() -> tuple[SenseVoiceComponentManager, Any]:
    manager = SenseVoiceComponentManager(
        MODELS_ROOT / "sensevoice-small-int8",
        MODELS_ROOT / "sensevoice-runtime",
        MODELS_ROOT.parent / "cache" / "sensevoice",
    )
    return manager, manager.verify()


def run_sensevoice_auto() -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    all_rows: list[dict[str, Any]] = []
    summaries: list[dict[str, Any]] = []
    manager, _paths = _sensevoice_paths()
    model_size = manager.status().installed_size
    profile = SpeechRecognitionProfile(
        id="benchmark-sensevoice-auto",
        name="SenseVoice auto",
        provider="local_offline",
        model="sensevoice-small-int8",
    )
    for spec in SENSEVOICE_LANGUAGES:
        adapter = SenseVoiceLocalSpeechRecognizer(manager, num_threads=4)
        rows: list[dict[str, Any]] = []
        cold_start_ms = 0.0
        with ResourceMonitor() as monitor:
            for index, item in enumerate(_manifest(spec)):
                samples, sample_rate = _read_wave(_audio_path(spec, item))
                started = time.perf_counter()
                try:
                    result = adapter.transcribe(
                        SpeechRecognitionRequest(
                            uuid4().hex,
                            _pcm16(samples),
                            sample_rate,
                            spec.code,
                        ),
                        profile,
                    )
                    elapsed = (time.perf_counter() - started) * 1000
                    if index == 0:
                        cold_start_ms = elapsed
                    row = _case_row(
                        "sensevoice_auto_current",
                        spec,
                        item,
                        result.text,
                        elapsed,
                        result.detected_language,
                    )
                except Exception as exc:
                    elapsed = (time.perf_counter() - started) * 1000
                    row = _case_row(
                        "sensevoice_auto_current",
                        spec,
                        item,
                        "",
                        elapsed,
                        status="failed",
                        error=str(getattr(exc, "user_message", type(exc).__name__))[:200],
                    )
                rows.append(row)
                all_rows.append(row)
            adapter.release(profile)
            gc.collect()
        summary = _summary(
            "sensevoice_auto_current",
            spec,
            rows,
            cold_start_ms,
            monitor.result(),
            model_size,
        )
        summaries.append(summary)
        print(
            f"[asr] SenseVoice auto {spec.code}: "
            f"{summary['primary_metric']}={summary['primary_error_rate']:.3f}, "
            f"RTF={summary['rtf']:.3f}",
            flush=True,
        )
    return all_rows, summaries


def run_sensevoice_forced() -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    all_rows: list[dict[str, Any]] = []
    summaries: list[dict[str, Any]] = []
    manager, paths = _sensevoice_paths()
    loader = SenseVoiceLocalSpeechRecognizer(manager, num_threads=4)
    module = loader._load_runtime(paths.runtime_root)
    for spec in SENSEVOICE_LANGUAGES:
        rows: list[dict[str, Any]] = []
        with ResourceMonitor() as monitor:
            load_started = time.perf_counter()
            recognizer = module.OfflineRecognizer.from_sense_voice(
                model=str(paths.model),
                tokens=str(paths.tokens),
                num_threads=4,
                language=spec.speech_code,
                use_itn=True,
                provider="cpu",
            )
            cold_start_ms = (time.perf_counter() - load_started) * 1000
            for item in _manifest(spec):
                samples, sample_rate = _read_wave(_audio_path(spec, item))
                started = time.perf_counter()
                try:
                    stream = recognizer.create_stream()
                    stream.accept_waveform(sample_rate, samples)
                    recognizer.decode_stream(stream)
                    raw = stream.result
                    hypothesis = SenseVoiceLocalSpeechRecognizer._normalize_text(
                        str(getattr(raw, "text", ""))
                    )
                    detected = {
                        "<|zh|>": "zh-CN",
                        "<|en|>": "en",
                        "<|ja|>": "ja",
                        "<|ko|>": "ko",
                    }.get(str(getattr(raw, "lang", "")).strip(), "")
                    elapsed = (time.perf_counter() - started) * 1000
                    row = _case_row(
                        "sensevoice_forced_candidate",
                        spec,
                        item,
                        hypothesis,
                        elapsed,
                        detected,
                    )
                except Exception as exc:
                    elapsed = (time.perf_counter() - started) * 1000
                    row = _case_row(
                        "sensevoice_forced_candidate",
                        spec,
                        item,
                        "",
                        elapsed,
                        status="failed",
                        error=type(exc).__name__,
                    )
                rows.append(row)
                all_rows.append(row)
            del recognizer
            gc.collect()
        summary = _summary(
            "sensevoice_forced_candidate",
            spec,
            rows,
            cold_start_ms,
            monitor.result(),
            manager.status().installed_size,
        )
        summaries.append(summary)
        print(
            f"[asr] SenseVoice forced {spec.code}: "
            f"{summary['primary_metric']}={summary['primary_error_rate']:.3f}, "
            f"RTF={summary['rtf']:.3f}",
            flush=True,
        )
    return all_rows, summaries


def _free_port() -> int:
    with socket.socket() as stream:
        stream.bind(("127.0.0.1", 0))
        return int(stream.getsockname()[1])


def _wait_for_server(process: subprocess.Popen[bytes], port: int) -> float:
    started = time.perf_counter()
    while time.perf_counter() - started < 120:
        if process.poll() is not None:
            raise RuntimeError(f"whisper server exited with {process.returncode}")
        with socket.socket() as stream:
            stream.settimeout(0.2)
            if stream.connect_ex(("127.0.0.1", port)) == 0:
                return (time.perf_counter() - started) * 1000
        time.sleep(0.05)
    raise TimeoutError("whisper server start timed out")


def run_whisper(
    model_path: Path = WHISPER_MODEL,
    engine_name: str = "whisper_cpp_base_q5_1",
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    if WHISPER_SERVER is None or not model_path.is_file():
        raise FileNotFoundError("whisper.cpp benchmark assets are missing")
    all_rows: list[dict[str, Any]] = []
    summaries: list[dict[str, Any]] = []
    runtime_size = sum(
        path.stat().st_size for path in WHISPER_SERVER.parent.glob("*.dll")
    ) + WHISPER_SERVER.stat().st_size
    model_size = model_path.stat().st_size + runtime_size
    creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    for spec in SPOKEN_LANGUAGES:
        assert spec.speech_code is not None
        port = _free_port()
        log_path = TMP_ROOT / f"whisper-server-{spec.code}.log"
        with log_path.open("wb") as log:
            process = subprocess.Popen(
                [
                    str(WHISPER_SERVER),
                    "-m",
                    str(model_path),
                    "-t",
                    "4",
                    "-ng",
                    "-l",
                    spec.speech_code,
                    "--host",
                    "127.0.0.1",
                    "--port",
                    str(port),
                ],
                cwd=str(WHISPER_SERVER.parent),
                stdout=log,
                stderr=subprocess.STDOUT,
                creationflags=creation_flags,
            )
            ps_process = psutil.Process(process.pid)
            rows: list[dict[str, Any]] = []
            with ResourceMonitor(ps_process) as monitor:
                cold_start_ms = _wait_for_server(process, port)
                with httpx.Client(timeout=180.0) as client:
                    for item in _manifest(spec):
                        path = _audio_path(spec, item)
                        started = time.perf_counter()
                        try:
                            with path.open("rb") as audio:
                                response = client.post(
                                    f"http://127.0.0.1:{port}/inference",
                                    files={"file": (path.name, audio, "audio/wav")},
                                    data={
                                        "response_format": "json",
                                        "temperature": "0.0",
                                        "language": spec.speech_code,
                                    },
                                )
                            response.raise_for_status()
                            payload = response.json()
                            hypothesis = str(payload.get("text", "")).strip()
                            elapsed = (time.perf_counter() - started) * 1000
                            row = _case_row(
                                engine_name,
                                spec,
                                item,
                                hypothesis,
                                elapsed,
                            )
                        except Exception as exc:
                            elapsed = (time.perf_counter() - started) * 1000
                            row = _case_row(
                                engine_name,
                                spec,
                                item,
                                "",
                                elapsed,
                                status="failed",
                                error=type(exc).__name__,
                            )
                        rows.append(row)
                        all_rows.append(row)
                resource_stats = monitor.result()
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
            summary = _summary(
                engine_name,
                spec,
                rows,
                cold_start_ms,
                resource_stats,
                model_size,
            )
            summaries.append(summary)
            print(
                f"[asr] whisper.cpp {spec.code}: "
                f"{summary['primary_metric']}={summary['primary_error_rate']:.3f}, "
                f"RTF={summary['rtf']:.3f}",
                flush=True,
            )
        log_path.unlink(missing_ok=True)
    return all_rows, summaries


def _existing_cases() -> list[dict[str, Any]]:
    path = RESULTS_ROOT / "asr_cases.csv"
    if not path.is_file():
        return []
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--small-only", action="store_true")
    args = parser.parse_args()
    ensure_directories()
    if args.small_only:
        cases = _existing_cases()
        summaries = read_json(RESULTS_ROOT / "asr_summary.json")
        cases = [
            row for row in cases if row.get("engine") != "whisper_cpp_small_q5_1"
        ]
        summaries = [
            row
            for row in summaries
            if row.get("engine") != "whisper_cpp_small_q5_1"
        ]
        new_cases, new_summaries = run_whisper(
            WHISPER_SMALL_MODEL,
            "whisper_cpp_small_q5_1",
        )
        cases.extend(new_cases)
        summaries.extend(new_summaries)
        write_csv(RESULTS_ROOT / "asr_cases.csv", cases)
        write_json(RESULTS_ROOT / "asr_summary.json", summaries)
        return 0
    cases: list[dict[str, Any]] = []
    summaries: list[dict[str, Any]] = []
    for runner in (run_sensevoice_auto, run_sensevoice_forced, run_whisper):
        new_cases, new_summaries = runner()
        cases.extend(new_cases)
        summaries.extend(new_summaries)
    write_csv(RESULTS_ROOT / "asr_cases.csv", cases)
    write_json(RESULTS_ROOT / "asr_summary.json", summaries)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
