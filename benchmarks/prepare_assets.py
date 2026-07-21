from __future__ import annotations

import argparse
import csv
import hashlib
import json
import random
import shutil
import tarfile
import zipfile
from dataclasses import asdict
from pathlib import Path
from typing import Any

import httpx

from benchmarks.benchmark_config import (
    ASR_SAMPLE_COUNT,
    CACHE_ROOT,
    FLEURS_ROOT,
    FLORES_ROOT,
    MODELS_ROOT,
    RANDOM_SEED,
    SENSEVOICE_LANGUAGES,
    SPOKEN_LANGUAGES,
    TOOLS_ROOT,
    ensure_directories,
)
from benchmarks.common import flores_lines, normalized_text, write_json
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager
from vrctranslate.infrastructure.speech.local_component_manager import (
    SenseVoiceComponentManager,
    _windows_trust_context,
)


FLEURS_API = "https://huggingface.co/api/datasets/google/fleurs/tree/main"
FLEURS_RESOLVE = "https://huggingface.co/datasets/google/fleurs/resolve/main"
WHISPER_RELEASE_API = "https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest"
WHISPER_MODEL_API = (
    "https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/main"
    "?recursive=true&expand=true"
)
WHISPER_MODEL_NAME = "ggml-base-q5_1.bin"


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _download(
    client: httpx.Client,
    url: str,
    destination: Path,
    *,
    size: int = 0,
    sha256: str = "",
) -> Path:
    if destination.is_file():
        valid_size = not size or destination.stat().st_size == size
        valid_digest = not sha256 or _sha256(destination) == sha256.casefold()
        if valid_size and valid_digest:
            print(f"[cached] {destination.name}")
            return destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".part")
    temporary.unlink(missing_ok=True)
    written = 0
    next_report = 10
    try:
        with client.stream("GET", url) as response:
            response.raise_for_status()
            with temporary.open("wb") as output:
                for chunk in response.iter_bytes(1024 * 1024):
                    if not chunk:
                        continue
                    output.write(chunk)
                    written += len(chunk)
                    if size:
                        percent = written * 100 // size
                        if percent >= next_report:
                            print(f"[download] {destination.name}: {percent}%")
                            next_report += 10
        if size and written != size:
            raise OSError(
                f"download size mismatch for {destination.name}: {written} != {size}"
            )
        if sha256 and _sha256(temporary) != sha256.casefold():
            raise OSError(f"download checksum mismatch for {destination.name}")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    return destination


def install_ocr_models() -> None:
    manager = OcrModelManager(
        MODELS_ROOT / "ocr",
        CACHE_ROOT / "ocr-models",
    )
    for language in ("zh-CN", "ja", "en", "ko", "latin", "cyrillic"):
        status = manager.status(language)
        if status.installed:
            print(f"[ocr] {language}: already installed")
            continue
        print(f"[ocr] installing {language} ({status.required_download_size} bytes)")
        manager.install(language)
    storage = manager.storage()
    write_json(
        MODELS_ROOT / "ocr-benchmark-manifest.json",
        {
            "packages": [asdict(status) for status in manager.statuses()],
            "shared_size": storage.shared_size,
            "total_size": storage.total_size,
        },
    )


def install_sensevoice() -> None:
    manager = SenseVoiceComponentManager(
        MODELS_ROOT / "sensevoice-small-int8",
        MODELS_ROOT / "sensevoice-runtime",
        CACHE_ROOT / "sensevoice",
    )
    status = manager.status()
    if not status.installed:
        print(f"[sensevoice] installing {status.required_download_size} bytes")
        manager.install()
    verified = manager.verify()
    write_json(
        MODELS_ROOT / "sensevoice-benchmark-manifest.json",
        {
            "version": manager.status().version,
            "installed_size": manager.status().installed_size,
            "model": str(verified.model.relative_to(MODELS_ROOT)),
            "tokens": str(verified.tokens.relative_to(MODELS_ROOT)),
            "runtime": str(verified.runtime_root.relative_to(MODELS_ROOT)),
        },
    )


def install_whisper() -> None:
    with httpx.Client(
        follow_redirects=True,
        timeout=600.0,
        verify=_windows_trust_context(),
    ) as client:
        release = client.get(WHISPER_RELEASE_API).json()
        asset = next(
            item for item in release["assets"] if item["name"] == "whisper-bin-x64.zip"
        )
        digest = str(asset.get("digest", "")).removeprefix("sha256:")
        archive = _download(
            client,
            asset["browser_download_url"],
            CACHE_ROOT / asset["name"],
            size=int(asset["size"]),
            sha256=digest,
        )
        runtime_root = TOOLS_ROOT / "whisper.cpp"
        if runtime_root.exists():
            shutil.rmtree(runtime_root)
        runtime_root.mkdir(parents=True)
        with zipfile.ZipFile(archive) as source:
            source.extractall(runtime_root)

        items = client.get(WHISPER_MODEL_API).json()
        model_item = next(item for item in items if item["path"] == WHISPER_MODEL_NAME)
        lfs = model_item["lfs"]
        model = _download(
            client,
            (
                "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/"
                f"{WHISPER_MODEL_NAME}?download=true"
            ),
            MODELS_ROOT / "whisper.cpp" / WHISPER_MODEL_NAME,
            size=int(lfs["size"]),
            sha256=str(lfs["oid"]),
        )
    binaries = sorted(path.name for path in runtime_root.rglob("*.exe"))
    write_json(
        MODELS_ROOT / "whisper-benchmark-manifest.json",
        {
            "release": release["tag_name"],
            "asset": asset["name"],
            "model": WHISPER_MODEL_NAME,
            "model_size": model.stat().st_size,
            "model_sha256": _sha256(model),
            "binaries": binaries,
            "note": (
                "The current official repository publishes base q5_1, not base q5_0; "
                "q5_1 is the closest maintained candidate and is tested explicitly."
            ),
        },
    )


def _fleurs_files(client: httpx.Client, code: str) -> dict[str, dict[str, Any]]:
    response = client.get(
        f"{FLEURS_API}/data/{code}?recursive=true&expand=false"
    )
    response.raise_for_status()
    return {
        str(item["path"]): item
        for item in response.json()
        if item.get("type") == "file"
    }


def _flores_match(language: str, text: str) -> tuple[str, int] | None:
    target = normalized_text(text)
    for split in ("dev", "devtest"):
        for index, line in enumerate(flores_lines(language, split)):
            if normalized_text(line) == target:
                return split, index
    return None


def _select_fleurs_rows(
    language: str,
    rows: list[list[str]],
) -> list[dict[str, Any]]:
    randomizer = random.Random(f"{RANDOM_SEED}:{language}")
    candidates = list(rows)
    randomizer.shuffle(candidates)
    selected: list[dict[str, Any]] = []
    sentence_ids: set[str] = set()
    for row in candidates:
        if len(row) < 7:
            continue
        sentence_id, filename, raw, normalized, _characters, samples, gender = row[:7]
        duration = int(samples) / 16_000.0
        if not 2.0 <= duration <= 12.0 or not 8 <= len(normalized) <= 180:
            continue
        if sentence_id in sentence_ids:
            continue
        match = _flores_match(language, raw)
        if match is None:
            continue
        split, index = match
        reference_language = "en" if language == "zh-CN" else "zh-CN"
        reference = flores_lines(reference_language, split)[index]
        selected.append(
            {
                "sentence_id": sentence_id,
                "filename": filename,
                "raw_transcription": raw,
                "normalized_transcription": normalized,
                "gender": gender,
                "duration_seconds": duration,
                "sample_rate": 16_000,
                "flores_split": split,
                "flores_index": index,
                "translation_target": reference_language,
                "translation_reference": reference,
            }
        )
        sentence_ids.add(sentence_id)
        if len(selected) >= ASR_SAMPLE_COUNT:
            return selected
    raise RuntimeError(f"not enough matched FLEURS rows for {language}: {len(selected)}")


def prepare_fleurs() -> None:
    with httpx.Client(
        follow_redirects=True,
        timeout=900.0,
        verify=_windows_trust_context(),
    ) as client:
        readme = _download(
            client,
            f"{FLEURS_RESOLVE}/README.md?download=true",
            CACHE_ROOT / "fleurs-README.md",
        )
        for spec in SPOKEN_LANGUAGES:
            assert spec.fleurs_code is not None
            code = spec.fleurs_code
            language_root = FLEURS_ROOT / code
            manifest_path = language_root / "manifest.json"
            if manifest_path.is_file():
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                if all((language_root / "audio" / item["filename"]).is_file() for item in manifest):
                    print(f"[fleurs] {code}: already prepared")
                    continue
            files = _fleurs_files(client, code)
            tsv_key = f"data/{code}/dev.tsv"
            tar_key = f"data/{code}/audio/dev.tar.gz"
            tsv_item = files[tsv_key]
            tar_item = files[tar_key]
            tsv_path = _download(
                client,
                f"{FLEURS_RESOLVE}/{tsv_key}?download=true",
                CACHE_ROOT / f"fleurs-{code}-dev.tsv",
                size=int(tsv_item["size"]),
            )
            with tsv_path.open("r", encoding="utf-8", newline="") as stream:
                rows = list(csv.reader(stream, delimiter="\t"))
            selected = _select_fleurs_rows(spec.code, rows)
            tar_lfs = tar_item.get("lfs") or {}
            archive = _download(
                client,
                f"{FLEURS_RESOLVE}/{tar_key}?download=true",
                CACHE_ROOT / f"fleurs-{code}-dev.tar.gz",
                size=int(tar_item["size"]),
                sha256=str(tar_lfs.get("oid", "")),
            )
            wanted = {item["filename"] for item in selected}
            audio_root = language_root / "audio"
            audio_root.mkdir(parents=True, exist_ok=True)
            extracted: set[str] = set()
            with tarfile.open(archive, "r:gz") as source:
                for member in source:
                    name = Path(member.name).name
                    if name not in wanted or not member.isfile():
                        continue
                    stream = source.extractfile(member)
                    if stream is None:
                        continue
                    destination = audio_root / name
                    with destination.open("wb") as output:
                        shutil.copyfileobj(stream, output)
                    extracted.add(name)
            if extracted != wanted:
                raise OSError(
                    f"missing FLEURS audio for {code}: {sorted(wanted - extracted)}"
                )
            write_json(manifest_path, selected)
            archive.unlink(missing_ok=True)
            print(f"[fleurs] {code}: prepared {len(selected)} clips")
        write_json(
            FLEURS_ROOT / "source.json",
            {
                "repository": "google/fleurs",
                "readme_sha256": _sha256(readme),
                "license": "CC-BY-4.0 (as declared by the dataset card)",
                "sample_count_per_language": ASR_SAMPLE_COUNT,
                "raw_archives_deleted_after_extraction": True,
            },
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ocr", action="store_true")
    parser.add_argument("--sensevoice", action="store_true")
    parser.add_argument("--whisper", action="store_true")
    parser.add_argument("--audio", action="store_true")
    parser.add_argument("--all", action="store_true")
    args = parser.parse_args()
    ensure_directories()
    selected = args.all or not any(
        (args.ocr, args.sensevoice, args.whisper, args.audio)
    )
    if args.all or args.ocr or selected:
        install_ocr_models()
    if args.all or args.sensevoice or selected:
        install_sensevoice()
    if args.all or args.whisper or selected:
        install_whisper()
    if args.all or args.audio or selected:
        prepare_fleurs()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
