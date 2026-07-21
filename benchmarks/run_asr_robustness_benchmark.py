from __future__ import annotations

import gc
import subprocess
import time
import wave
from pathlib import Path
from typing import Any

import httpx
import numpy as np

from benchmarks.benchmark_config import (
    MODELS_ROOT,
    RESULTS_ROOT,
    SENSEVOICE_LANGUAGES,
    SPOKEN_LANGUAGES,
    TMP_ROOT,
    ensure_directories,
)
from benchmarks.common import (
    character_error_rate,
    mean,
    normalized_text,
    percentile,
    word_error_rate,
    write_csv,
    write_json,
)
from benchmarks.run_asr_benchmark import (
    WHISPER_SERVER,
    WHISPER_SMALL_MODEL,
    _audio_path,
    _free_port,
    _manifest,
    _read_wave,
    _wait_for_server,
)
from vrctranslate.infrastructure.speech.local_component_manager import (
    SenseVoiceComponentManager,
)
from vrctranslate.infrastructure.speech.sensevoice_local import (
    SenseVoiceLocalSpeechRecognizer,
)


CONDITIONS = ("clean", "low_volume", "noise_10db", "fast_1_15x")
SAMPLES_PER_LANGUAGE = 10


def _transform(samples: np.ndarray, condition: str, seed: int) -> np.ndarray:
    if condition == "low_volume":
        return np.ascontiguousarray(samples * 0.25)
    if condition == "noise_10db":
        signal_rms = max(1e-7, float(np.sqrt(np.mean(samples**2))))
        noise_rms = signal_rms / (10 ** (10 / 20))
        noise = np.random.default_rng(seed).normal(0, noise_rms, samples.shape)
        return np.ascontiguousarray(np.clip(samples + noise, -1, 1).astype(np.float32))
    if condition == "fast_1_15x":
        target = max(1, round(len(samples) / 1.15))
        positions = np.linspace(0, len(samples) - 1, target)
        return np.interp(positions, np.arange(len(samples)), samples).astype(np.float32)
    return samples


def _row(
    engine: str,
    spec: Any,
    item: dict[str, Any],
    condition: str,
    hypothesis: str,
    elapsed_ms: float,
    duration: float,
) -> dict[str, Any]:
    reference = str(item["raw_transcription"])
    return {
        "engine": engine,
        "language": spec.code,
        "condition": condition,
        "filename": item["filename"],
        "reference": reference,
        "hypothesis": hypothesis,
        "duration_seconds": duration,
        "latency_ms": elapsed_ms,
        "rtf": elapsed_ms / 1000 / duration,
        "cer": character_error_rate(reference, hypothesis),
        "wer": word_error_rate(reference, hypothesis),
        "exact": normalized_text(reference) == normalized_text(hypothesis),
    }


def _summaries(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    output: list[dict[str, Any]] = []
    keys = sorted({(row["engine"], row["language"], row["condition"]) for row in rows})
    cjk = {item.code for item in SPOKEN_LANGUAGES if item.cjk_metric}
    for engine, language, condition in keys:
        subset = [
            row
            for row in rows
            if row["engine"] == engine
            and row["language"] == language
            and row["condition"] == condition
        ]
        metric = "cer" if language in cjk else "wer"
        output.append(
            {
                "engine": engine,
                "language": language,
                "condition": condition,
                "cases": len(subset),
                "primary_metric": metric,
                "primary_error_rate": mean(float(row[metric]) for row in subset),
                "exact_rate": mean(1.0 if row["exact"] else 0.0 for row in subset),
                "rtf": mean(float(row["rtf"]) for row in subset),
                "latency_p50_ms": percentile(
                    (float(row["latency_ms"]) for row in subset), 50
                ),
            }
        )
    return output


def run_sensevoice() -> list[dict[str, Any]]:
    manager = SenseVoiceComponentManager(
        MODELS_ROOT / "sensevoice-small-int8",
        MODELS_ROOT / "sensevoice-runtime",
        MODELS_ROOT.parent / "cache" / "sensevoice",
    )
    paths = manager.verify()
    loader = SenseVoiceLocalSpeechRecognizer(manager, num_threads=4)
    module = loader._load_runtime(paths.runtime_root)
    rows: list[dict[str, Any]] = []
    for spec in SENSEVOICE_LANGUAGES:
        recognizer = module.OfflineRecognizer.from_sense_voice(
            model=str(paths.model),
            tokens=str(paths.tokens),
            num_threads=4,
            language=spec.speech_code,
            use_itn=True,
            provider="cpu",
        )
        for sample_index, item in enumerate(_manifest(spec)[:SAMPLES_PER_LANGUAGE]):
            samples, sample_rate = _read_wave(_audio_path(spec, item))
            for condition in CONDITIONS:
                transformed = _transform(samples, condition, sample_index)
                started = time.perf_counter()
                stream = recognizer.create_stream()
                stream.accept_waveform(sample_rate, transformed)
                recognizer.decode_stream(stream)
                hypothesis = SenseVoiceLocalSpeechRecognizer._normalize_text(
                    str(stream.result.text)
                )
                elapsed = (time.perf_counter() - started) * 1000
                rows.append(
                    _row(
                        "sensevoice_forced_candidate",
                        spec,
                        item,
                        condition,
                        hypothesis,
                        elapsed,
                        len(transformed) / sample_rate,
                    )
                )
        del recognizer
        gc.collect()
        print(f"[asr-stress] SenseVoice {spec.code}: complete", flush=True)
    return rows


def _write_pcm_wave(path: Path, samples: np.ndarray, sample_rate: int) -> None:
    pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(pcm)


def run_whisper_small() -> list[dict[str, Any]]:
    if WHISPER_SERVER is None or not WHISPER_SMALL_MODEL.is_file():
        raise FileNotFoundError("whisper.cpp small benchmark assets missing")
    rows: list[dict[str, Any]] = []
    european = [
        item for item in SPOKEN_LANGUAGES if item.code in {"fr", "de", "es", "ru"}
    ]
    creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    for spec in european:
        port = _free_port()
        log_path = TMP_ROOT / f"whisper-stress-{spec.code}.log"
        with log_path.open("wb") as log:
            process = subprocess.Popen(
                [
                    str(WHISPER_SERVER),
                    "-m",
                    str(WHISPER_SMALL_MODEL),
                    "-t",
                    "4",
                    "-ng",
                    "-l",
                    str(spec.speech_code),
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
            _wait_for_server(process, port)
            with httpx.Client(timeout=180.0) as client:
                for sample_index, item in enumerate(
                    _manifest(spec)[:SAMPLES_PER_LANGUAGE]
                ):
                    samples, sample_rate = _read_wave(_audio_path(spec, item))
                    for condition in CONDITIONS:
                        transformed = _transform(samples, condition, sample_index)
                        temporary = TMP_ROOT / (
                            f"asr-stress-{spec.code}-{sample_index}-{condition}.wav"
                        )
                        _write_pcm_wave(temporary, transformed, sample_rate)
                        started = time.perf_counter()
                        try:
                            with temporary.open("rb") as audio:
                                response = client.post(
                                    f"http://127.0.0.1:{port}/inference",
                                    files={
                                        "file": (temporary.name, audio, "audio/wav")
                                    },
                                    data={
                                        "response_format": "json",
                                        "temperature": "0.0",
                                        "language": str(spec.speech_code),
                                    },
                                )
                            response.raise_for_status()
                            hypothesis = str(response.json().get("text", "")).strip()
                        finally:
                            temporary.unlink(missing_ok=True)
                        elapsed = (time.perf_counter() - started) * 1000
                        rows.append(
                            _row(
                                "whisper_cpp_small_q5_1",
                                spec,
                                item,
                                condition,
                                hypothesis,
                                elapsed,
                                len(transformed) / sample_rate,
                            )
                        )
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        log_path.unlink(missing_ok=True)
        print(f"[asr-stress] whisper.cpp small {spec.code}: complete", flush=True)
    return rows


def main() -> int:
    ensure_directories()
    rows = [*run_sensevoice(), *run_whisper_small()]
    write_csv(RESULTS_ROOT / "asr_robustness_cases.csv", rows)
    write_json(RESULTS_ROOT / "asr_robustness_summary.json", _summaries(rows))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
