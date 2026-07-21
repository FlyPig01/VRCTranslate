from __future__ import annotations

import csv
import gc
import json
import math
import platform
import re
import statistics
import threading
import time
import unicodedata
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable

import psutil

from benchmarks.benchmark_config import (
    FLORES_ROOT,
    LANGUAGE_BY_CODE,
    RANDOM_SEED,
    ensure_directories,
)


_WHITESPACE = re.compile(r"\s+")
_PUNCTUATION = re.compile(r"[^\w\s]", re.UNICODE)


def normalized_text(value: str, *, keep_punctuation: bool = False) -> str:
    text = unicodedata.normalize("NFKC", value).casefold().strip()
    if not keep_punctuation:
        text = _PUNCTUATION.sub(" ", text)
    return _WHITESPACE.sub(" ", text).strip()


def levenshtein(reference: str, hypothesis: str) -> int:
    if len(reference) < len(hypothesis):
        reference, hypothesis = hypothesis, reference
    previous = list(range(len(hypothesis) + 1))
    for row, ref_item in enumerate(reference, 1):
        current = [row]
        for column, hyp_item in enumerate(hypothesis, 1):
            current.append(
                min(
                    current[-1] + 1,
                    previous[column] + 1,
                    previous[column - 1] + (ref_item != hyp_item),
                )
            )
        previous = current
    return previous[-1]


def character_error_rate(reference: str, hypothesis: str) -> float:
    ref = normalized_text(reference).replace(" ", "")
    hyp = normalized_text(hypothesis).replace(" ", "")
    if not ref:
        return 0.0 if not hyp else 1.0
    return levenshtein(ref, hyp) / len(ref)


def word_error_rate(reference: str, hypothesis: str) -> float:
    ref = normalized_text(reference).split()
    hyp = normalized_text(hypothesis).split()
    if not ref:
        return 0.0 if not hyp else 1.0
    return levenshtein(ref, hyp) / len(ref)


def percentile(values: Iterable[float], percent: float) -> float:
    ordered = sorted(float(value) for value in values)
    if not ordered:
        return 0.0
    if len(ordered) == 1:
        return ordered[0]
    index = (len(ordered) - 1) * percent / 100.0
    lower = math.floor(index)
    upper = math.ceil(index)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower)


def mean(values: Iterable[float]) -> float:
    items = list(values)
    return statistics.fmean(items) if items else 0.0


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = sorted({key for row in rows for key in row})
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def flores_lines(language: str, split: str = "devtest") -> list[str]:
    spec = LANGUAGE_BY_CODE[language]
    path = FLORES_ROOT / split / f"{spec.flores_code}.{split}"
    return path.read_text(encoding="utf-8").splitlines()


def aligned_sample_indices(count: int, population: int) -> list[int]:
    import random

    if count > population:
        raise ValueError("sample count exceeds corpus")
    return sorted(random.Random(RANDOM_SEED).sample(range(population), count))


@dataclass(slots=True)
class ResourceStats:
    elapsed_seconds: float
    cpu_seconds: float
    average_cpu_percent: float
    peak_rss_mib: float
    rss_delta_mib: float


class ResourceMonitor:
    def __init__(self, process: psutil.Process | None = None) -> None:
        self._process = process or psutil.Process()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._peak_rss = 0
        self._start_rss = 0
        self._start_cpu = 0.0
        self._started = 0.0

    def __enter__(self) -> ResourceMonitor:
        gc.collect()
        self._started = time.perf_counter()
        self._start_cpu = self._cpu_seconds()
        self._start_rss = self._rss()
        self._peak_rss = self._start_rss
        self._thread = threading.Thread(target=self._sample, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)

    def result(self) -> ResourceStats:
        elapsed = max(0.000001, time.perf_counter() - self._started)
        cpu_seconds = max(0.0, self._cpu_seconds() - self._start_cpu)
        cpu_percent = cpu_seconds / elapsed / max(1, psutil.cpu_count()) * 100.0
        return ResourceStats(
            elapsed,
            cpu_seconds,
            cpu_percent,
            self._peak_rss / (1024 * 1024),
            max(0, self._peak_rss - self._start_rss) / (1024 * 1024),
        )

    def _sample(self) -> None:
        while not self._stop.wait(0.05):
            self._peak_rss = max(self._peak_rss, self._rss())

    def _processes(self) -> list[psutil.Process]:
        try:
            return [self._process, *self._process.children(recursive=True)]
        except psutil.Error:
            return [self._process]

    def _rss(self) -> int:
        total = 0
        for process in self._processes():
            try:
                total += process.memory_info().rss
            except psutil.Error:
                continue
        return total

    def _cpu_seconds(self) -> float:
        total = 0.0
        for process in self._processes():
            try:
                times = process.cpu_times()
                total += times.user + times.system
            except psutil.Error:
                continue
        return total


def machine_metadata() -> dict[str, Any]:
    memory = psutil.virtual_memory()
    disk = psutil.disk_usage(str(Path(__file__).anchor or "E:\\"))
    return {
        "platform": platform.platform(),
        "processor": platform.processor(),
        "logical_cpu_count": psutil.cpu_count(),
        "physical_cpu_count": psutil.cpu_count(logical=False),
        "memory_total_gib": round(memory.total / 2**30, 2),
        "memory_available_gib_at_start": round(memory.available / 2**30, 2),
        "workspace_drive_free_gib": round(disk.free / 2**30, 2),
    }


def dataclass_dict(value: Any) -> dict[str, Any]:
    return asdict(value)
