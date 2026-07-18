from __future__ import annotations

import hashlib
import json
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from threading import Lock

import httpx

from vrctranslate.application.ports.ocr_models import (
    OcrModelProgress,
    OcrModelStatus,
    OcrModelStorage,
)
from vrctranslate.infrastructure.ocr.model_catalog import (
    OCR_MODEL_PACKAGES,
    OcrModelFile,
    OcrModelPackage,
)


@dataclass(frozen=True, slots=True)
class OcrModelPaths:
    language: str
    detection: Path
    orientation: Path
    recognition: Path
    recognition_version: str
    recognition_type: str


class OcrModelManager:
    """Install verified OCR models only inside the portable data directory."""

    def __init__(
        self,
        models_root: Path,
        cache_root: Path,
        *,
        packages: dict[str, OcrModelPackage] | None = None,
        client_factory: Callable[[], httpx.Client] | None = None,
    ) -> None:
        self.models_root = models_root
        self.cache_root = cache_root
        self._packages = packages or OCR_MODEL_PACKAGES
        self._management_lock = Lock()
        self._client_factory = client_factory or (
            lambda: httpx.Client(follow_redirects=True, timeout=120.0)
        )

    @property
    def manifest_path(self) -> Path:
        return self.models_root / "manifest.json"

    def statuses(self) -> list[OcrModelStatus]:
        return [self.status(language) for language in self._packages]

    def status(self, language: str) -> OcrModelStatus:
        package = self._package(language)
        paths = [self.models_root / item.relative_path for item in package.files]
        installed = all(
            path.is_file() and path.stat().st_size == item.size
            for path, item in zip(paths, package.files)
        )
        installed_size = sum(path.stat().st_size for path in paths if path.is_file())
        recognition = paths[-1]
        exclusive_size = recognition.stat().st_size if recognition.is_file() else 0
        required_download_size = sum(
            item.size
            for path, item in zip(paths, package.files)
            if not path.is_file() or path.stat().st_size != item.size
        )
        return OcrModelStatus(
            package.language,
            package.version,
            installed,
            package.download_size,
            installed_size,
            exclusive_size,
            required_download_size,
        )

    def storage(self) -> OcrModelStorage:
        files: dict[str, OcrModelFile] = {}
        for package in self._packages.values():
            for item in package.files:
                files[item.relative_path] = item
        total = sum(
            path.stat().st_size
            for relative in files
            if (path := self.models_root / relative).is_file()
        )
        shared = sum(
            path.stat().st_size
            for relative in (DETECTION_PATH, ORIENTATION_PATH)
            if (path := self.models_root / relative).is_file()
        )
        return OcrModelStorage(shared, total)

    def install(
        self,
        language: str,
        progress: OcrModelProgress | None = None,
    ) -> OcrModelStatus:
        with self._management_lock:
            return self._install(language, progress)

    def _install(
        self,
        language: str,
        progress: OcrModelProgress | None = None,
    ) -> OcrModelStatus:
        package = self._package(language)
        self.models_root.mkdir(parents=True, exist_ok=True)
        self.cache_root.mkdir(parents=True, exist_ok=True)
        total = package.download_size
        completed = 0
        if progress:
            progress(0, total)
        with self._client_factory() as client:
            for item in package.files:
                destination = self.models_root / item.relative_path
                if self._valid_file(destination, item):
                    completed += item.size
                    if progress:
                        progress(completed, total)
                    continue
                self._download_file(client, item, destination, completed, total, progress)
                completed += item.size
        self._write_manifest()
        return self.status(language)

    def remove(self, language: str) -> OcrModelStatus:
        with self._management_lock:
            return self._remove(language)

    def _remove(self, language: str) -> OcrModelStatus:
        package = self._package(language)
        recognition = self.models_root / package.files[-1].relative_path
        recognition.unlink(missing_ok=True)
        parent = recognition.parent
        if parent.exists() and not any(parent.iterdir()):
            parent.rmdir()
        other_installed = any(
            self.status(item).installed for item in self._packages if item != language
        )
        if not other_installed:
            for shared in (DETECTION_PATH, ORIENTATION_PATH):
                (self.models_root / shared).unlink(missing_ok=True)
            shared_dir = self.models_root / "shared"
            if shared_dir.exists() and not any(shared_dir.iterdir()):
                shared_dir.rmdir()
        self._write_manifest()
        return self.status(language)

    def paths(self, language: str) -> OcrModelPaths:
        package = self._package(language)
        if not self.status(language).installed:
            raise FileNotFoundError(language)
        return OcrModelPaths(
            language,
            self.models_root / DETECTION_PATH,
            self.models_root / ORIENTATION_PATH,
            self.models_root / package.files[-1].relative_path,
            package.recognition_version,
            package.recognition_type,
        )

    def signature(self, language: str) -> tuple[tuple[str, int, int], ...]:
        try:
            paths = self.paths(language)
        except FileNotFoundError:
            return ()
        files = (paths.detection, paths.orientation, paths.recognition)
        return tuple(
            (str(path), path.stat().st_size, path.stat().st_mtime_ns) for path in files
        )

    def detection_path(self) -> Path:
        path = self.models_root / DETECTION_PATH
        spec = next(
            (
                item
                for package in self._packages.values()
                for item in package.files
                if item.relative_path == DETECTION_PATH
            ),
            None,
        )
        if spec is None or not path.is_file() or path.stat().st_size != spec.size:
            raise FileNotFoundError(DETECTION_PATH)
        return path

    def detection_signature(self) -> tuple[str, int, int] | None:
        try:
            path = self.detection_path()
        except FileNotFoundError:
            return None
        stat = path.stat()
        return str(path), stat.st_size, stat.st_mtime_ns

    def _package(self, language: str) -> OcrModelPackage:
        normalized = "zh-CN" if language in {"zh", "zh_CN", "zh-CN"} else language
        if normalized == "auto":
            normalized = "ja"
        try:
            return self._packages[normalized]
        except KeyError as exc:
            raise ValueError(f"unsupported OCR language: {language}") from exc

    @staticmethod
    def _valid_file(path: Path, spec: OcrModelFile) -> bool:
        if not path.is_file() or path.stat().st_size != spec.size:
            return False
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest().lower() == spec.sha256.lower()

    def _download_file(
        self,
        client: httpx.Client,
        spec: OcrModelFile,
        destination: Path,
        completed: int,
        total: int,
        progress: OcrModelProgress | None,
    ) -> None:
        temporary = self.cache_root / f"{spec.sha256}.part"
        temporary.unlink(missing_ok=True)
        digest = hashlib.sha256()
        written = 0
        try:
            with client.stream("GET", spec.url) as response:
                response.raise_for_status()
                with temporary.open("wb") as output:
                    for chunk in response.iter_bytes(1024 * 1024):
                        if not chunk:
                            continue
                        output.write(chunk)
                        digest.update(chunk)
                        written += len(chunk)
                        if progress:
                            progress(min(total, completed + written), total)
            if written != spec.size:
                raise OSError(f"OCR 模型大小校验失败：{spec.relative_path}")
            if digest.hexdigest().lower() != spec.sha256.lower():
                raise OSError(f"OCR 模型 SHA-256 校验失败：{spec.relative_path}")
            if progress:
                progress(min(total, completed + written), total)
            destination.parent.mkdir(parents=True, exist_ok=True)
            temporary.replace(destination)
        finally:
            temporary.unlink(missing_ok=True)

    def _write_manifest(self) -> None:
        self.models_root.mkdir(parents=True, exist_ok=True)
        installed = {
            language: {
                "version": package.version,
                "installed": self.status(language).installed,
            }
            for language, package in self._packages.items()
        }
        temporary = self.manifest_path.with_suffix(".json.tmp")
        temporary.write_text(
            json.dumps({"schema": 1, "packages": installed}, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        temporary.replace(self.manifest_path)


DETECTION_PATH = "shared/detection.onnx"
ORIENTATION_PATH = "shared/orientation.onnx"
