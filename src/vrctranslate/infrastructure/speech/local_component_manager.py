from __future__ import annotations

import hashlib
import json
import shutil
import ssl
import sys
import zipfile
from collections.abc import Callable
from pathlib import Path, PurePosixPath
from threading import Lock
from typing import Protocol
from uuid import uuid4

import httpx

from vrctranslate.application.ports.speech_models import (
    SpeechModelPaths,
    SpeechModelProgress,
    SpeechModelStatus,
)
from vrctranslate.infrastructure.speech.local_component_catalog import (
    INSTALL_STAGING_BYTES,
    MODEL_ID,
    MODEL_VERSION,
    RUNTIME_VERSION,
    SPEECH_COMPONENT_FILES,
    SpeechComponentFile,
)
from vrctranslate.infrastructure.speech.download_source_selector import (
    AdaptiveDownloadSourceSelector,
)


class _Digest(Protocol):
    def update(self, data: bytes, /) -> object: ...

    def hexdigest(self) -> str: ...


class SenseVoiceComponentManager:
    """Install the verified CPU runtime and model inside portable data."""

    def __init__(
        self,
        models_root: Path,
        runtime_root: Path,
        cache_root: Path,
        *,
        files: tuple[SpeechComponentFile, ...] | None = None,
        client_factory: Callable[[], httpx.Client] | None = None,
        source_selector: AdaptiveDownloadSourceSelector | None = None,
    ) -> None:
        self.models_root = models_root
        self.runtime_root = runtime_root
        self.cache_root = cache_root
        self._files = files or SPEECH_COMPONENT_FILES
        self._client_factory = client_factory or (
            lambda: httpx.Client(
                follow_redirects=True,
                timeout=120.0,
                verify=_windows_trust_context(),
            )
        )
        self._source_selector = source_selector or AdaptiveDownloadSourceSelector()
        self._management_lock = Lock()
        self._cleanup_pending_removal()

    @property
    def model_manifest_path(self) -> Path:
        return self.models_root / "manifest.json"

    @property
    def runtime_manifest_path(self) -> Path:
        return self.runtime_root / "component.json"

    @property
    def removal_marker_path(self) -> Path:
        return self.runtime_root.with_name(self.runtime_root.name + ".remove-pending")

    def status(self) -> SpeechModelStatus:
        removal_pending = self.removal_marker_path.exists()
        installed = (
            not removal_pending
            and self._quick_model_ready()
            and self._quick_runtime_ready()
        )
        installed_size = self._directory_size(self.models_root) + self._directory_size(
            self.runtime_root
        )
        return SpeechModelStatus(
            model_id=MODEL_ID,
            version=f"{MODEL_VERSION} · {RUNTIME_VERSION}",
            installed=installed,
            download_size=self._download_size,
            installed_size=installed_size,
            required_download_size=0 if installed else self._download_size,
            models_root=self.models_root,
            removal_pending=removal_pending,
        )

    def install(
        self,
        progress: SpeechModelProgress | None = None,
    ) -> SpeechModelStatus:
        with self._management_lock:
            if self.removal_marker_path.exists():
                raise OSError("本地语音组件正在等待重启后删除")
            return self._install(progress)

    def _install(
        self,
        progress: SpeechModelProgress | None,
    ) -> SpeechModelStatus:
        if sys.platform != "win32" or sys.version_info[:2] != (3, 11):
            raise OSError("本地语音组件仅支持 Windows x64 与 Python 3.11")
        if sys.maxsize <= 2**32:
            raise OSError("本地语音组件仅支持 64 位 Windows")
        self.cache_root.mkdir(parents=True, exist_ok=True)
        total = self._download_size
        required_space = total + INSTALL_STAGING_BYTES
        free_space = shutil.disk_usage(self.cache_root).free
        if free_space < required_space:
            mib = 1024 * 1024
            required_mib = (required_space + mib - 1) // mib
            raise OSError(
                f"本地语音组件安装空间不足，当前磁盘至少需要 {required_mib} MiB 可用空间"
            )
        completed = 0
        if progress:
            progress(0, total)
        downloaded: dict[SpeechComponentFile, Path] = {}
        with self._client_factory() as client:
            for spec in self._files:
                path = self._download(client, spec, completed, total, progress)
                downloaded[spec] = path
                completed += spec.size

        staging = self.cache_root / f"install-{uuid4().hex}"
        model_staging = staging / "model"
        runtime_staging = staging / "runtime"
        try:
            model_staging.mkdir(parents=True)
            runtime_staging.mkdir(parents=True)
            for spec, source in downloaded.items():
                if spec.kind == "model":
                    destination = model_staging / spec.relative_path
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, destination)
                elif spec.wheel:
                    self._extract_runtime_wheel(source, runtime_staging)
            self._write_json(
                model_staging / "manifest.json",
                {
                    "schema": 1,
                    "model_id": MODEL_ID,
                    "version": MODEL_VERSION,
                    "files": self._manifest_files("model"),
                },
            )
            self._write_json(
                runtime_staging / "component.json",
                {
                    "schema": 1,
                    "version": RUNTIME_VERSION,
                    "source_files": self._manifest_files("runtime"),
                    "runtime_files": self._runtime_files(runtime_staging),
                },
            )
            self._assert_staged_runtime(runtime_staging)
            self._promote(runtime_staging, self.runtime_root)
            self._promote(model_staging, self.models_root)
        finally:
            shutil.rmtree(staging, ignore_errors=True)
        for path in downloaded.values():
            path.unlink(missing_ok=True)
        self.verify()
        if progress:
            progress(total, total)
        return self.status()

    def remove(self) -> SpeechModelStatus:
        with self._management_lock:
            pending = False
            for root in (self.models_root, self.runtime_root):
                if not root.exists():
                    continue
                try:
                    shutil.rmtree(root)
                except OSError:
                    pending = True
            if pending:
                self.removal_marker_path.parent.mkdir(parents=True, exist_ok=True)
                self.removal_marker_path.write_text(
                    "The local ASR component will be removed at next startup.\n",
                    encoding="utf-8",
                )
            else:
                self.removal_marker_path.unlink(missing_ok=True)
            for spec in self._files:
                (self.cache_root / f"{spec.sha256}.part").unlink(missing_ok=True)
                (self.cache_root / f"{spec.sha256}.download").unlink(missing_ok=True)
            return self.status()

    def _cleanup_pending_removal(self) -> None:
        if not self.removal_marker_path.is_file():
            return
        try:
            for root in (self.models_root, self.runtime_root):
                if root.exists():
                    shutil.rmtree(root)
            self.removal_marker_path.unlink(missing_ok=True)
        except OSError:
            # A running process may still hold a native DLL. The marker is
            # deliberately retained for the next clean application start.
            return

    def paths(self) -> SpeechModelPaths:
        if not self.status().installed:
            raise FileNotFoundError("SenseVoice 本地语音模型尚未安装")
        return SpeechModelPaths(
            model=self.models_root / "model.int8.onnx",
            tokens=self.models_root / "tokens.txt",
            runtime_root=self.runtime_root,
        )

    def verify(self) -> SpeechModelPaths:
        paths = self.paths()
        for spec in self._files:
            if spec.kind != "model":
                continue
            path = self.models_root / spec.relative_path
            if not self._valid_file(path, spec):
                raise OSError(f"本地语音模型校验失败：{spec.relative_path}")
        self._assert_staged_runtime(self.runtime_root)
        try:
            runtime_manifest = json.loads(
                self.runtime_manifest_path.read_text(encoding="utf-8")
            )
        except (OSError, ValueError) as exc:
            raise OSError("本地语音运行库校验清单无效") from exc
        if runtime_manifest.get("version") != RUNTIME_VERSION:
            raise OSError("本地语音运行库版本不匹配")
        runtime_files = runtime_manifest.get("runtime_files")
        if not isinstance(runtime_files, list) or not runtime_files:
            raise OSError("本地语音运行库缺少完整校验清单，请重新安装组件")
        for item in runtime_files:
            if not isinstance(item, dict):
                raise OSError("本地语音运行库校验清单无效")
            relative = PurePosixPath(str(item.get("path", "")))
            if relative.is_absolute() or ".." in relative.parts:
                raise OSError("本地语音运行库校验路径无效")
            path = self.runtime_root.joinpath(*relative.parts)
            try:
                expected_size = int(item.get("size", -1))
            except (TypeError, ValueError) as exc:
                raise OSError("本地语音运行库校验清单无效") from exc
            expected_sha = str(item.get("sha256", ""))
            if not path.is_file() or path.stat().st_size != expected_size:
                raise OSError(f"本地语音运行库文件缺失：{relative.as_posix()}")
            digest = hashlib.sha256()
            self._update_digest(digest, path)
            if digest.hexdigest().lower() != expected_sha.lower():
                raise OSError(f"本地语音运行库校验失败：{relative.as_posix()}")
        return paths

    def _download(
        self,
        client: httpx.Client,
        spec: SpeechComponentFile,
        completed: int,
        total: int,
        progress: SpeechModelProgress | None,
    ) -> Path:
        complete = self.cache_root / f"{spec.sha256}.download"
        if self._valid_file(complete, spec):
            if progress:
                progress(completed + spec.size, total)
            return complete
        complete.unlink(missing_ok=True)
        partial = self.cache_root / f"{spec.sha256}.part"
        last_error: httpx.HTTPError | None = None
        urls = self._source_selector.order(
            client,
            (spec.url, *spec.fallback_urls),
        )
        for url in urls:
            try:
                written, digest = self._transfer(
                    client,
                    url,
                    partial,
                    spec,
                    completed,
                    total,
                    progress,
                )
                break
            except httpx.HTTPError as exc:
                last_error = exc
        else:
            raise OSError(
                "本地语音组件下载失败，请检查网络、代理或系统证书后重试"
            ) from last_error
        if written != spec.size:
            partial.unlink(missing_ok=True)
            raise OSError(f"本地语音组件大小校验失败：{spec.relative_path}")
        if digest.hexdigest().lower() != spec.sha256.lower():
            partial.unlink(missing_ok=True)
            raise OSError(f"本地语音组件 SHA-256 校验失败：{spec.relative_path}")
        partial.replace(complete)
        return complete

    @staticmethod
    def _transfer(
        client: httpx.Client,
        url: str,
        partial: Path,
        spec: SpeechComponentFile,
        completed: int,
        total: int,
        progress: SpeechModelProgress | None,
    ) -> tuple[int, _Digest]:
        offset = partial.stat().st_size if partial.is_file() else 0
        if offset > spec.size:
            partial.unlink(missing_ok=True)
            offset = 0
        digest = hashlib.sha256()
        if offset:
            self._update_digest(digest, partial)
        headers = {"Range": f"bytes={offset}-"} if offset else None
        with client.stream("GET", url, headers=headers) as response:
            response.raise_for_status()
            append = offset > 0 and response.status_code == 206
            if not append:
                offset = 0
                digest = hashlib.sha256()
            mode = "ab" if append else "wb"
            written = offset
            with partial.open(mode) as output:
                for chunk in response.iter_bytes(1024 * 1024):
                    if not chunk:
                        continue
                    output.write(chunk)
                    digest.update(chunk)
                    written += len(chunk)
                    if progress:
                        progress(min(total, completed + written), total)
        return written, digest

    @staticmethod
    def _extract_runtime_wheel(wheel_path: Path, destination: Path) -> None:
        with zipfile.ZipFile(wheel_path) as archive:
            for member in archive.infolist():
                relative = PurePosixPath(member.filename)
                if member.is_dir():
                    continue
                if relative.is_absolute() or ".." in relative.parts:
                    raise OSError("本地语音运行库压缩包包含非法路径")
                if not SenseVoiceComponentManager._keep_runtime_member(relative):
                    continue
                output = destination.joinpath(*relative.parts)
                output.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(member) as source, output.open("wb") as target:
                    shutil.copyfileobj(source, target)

    @staticmethod
    def _keep_runtime_member(path: PurePosixPath) -> bool:
        if not path.parts:
            return False
        root = path.parts[0]
        if root == "sherpa_onnx":
            if "include" in path.parts or "__pycache__" in path.parts:
                return False
            return path.suffix.casefold() != ".lib"
        if root.endswith(".dist-info"):
            return path.name in {"METADATA", "WHEEL", "LICENSE", "LICENSE.txt"} or (
                "licenses" in path.parts
            )
        return False

    @staticmethod
    def _assert_staged_runtime(root: Path) -> None:
        package = root / "sherpa_onnx"
        library = package / "lib"
        if not (package / "__init__.py").is_file():
            raise OSError("本地语音 Python 运行库不完整")
        if not any(library.glob("_sherpa_onnx*.pyd")):
            raise OSError("本地语音原生扩展缺失")
        for name in ("onnxruntime.dll", "sherpa-onnx-c-api.dll"):
            if not (library / name).is_file():
                raise OSError(f"本地语音运行库缺少 {name}")

    def _quick_model_ready(self) -> bool:
        if not self.model_manifest_path.is_file():
            return False
        return all(
            (self.models_root / spec.relative_path).is_file()
            and (self.models_root / spec.relative_path).stat().st_size == spec.size
            for spec in self._files
            if spec.kind == "model"
        )

    def _quick_runtime_ready(self) -> bool:
        if not self.runtime_manifest_path.is_file():
            return False
        try:
            self._assert_staged_runtime(self.runtime_root)
        except OSError:
            return False
        return True

    def _manifest_files(self, kind: str) -> list[dict[str, object]]:
        return [
            {
                "path": spec.relative_path,
                "size": spec.size,
                "sha256": spec.sha256,
            }
            for spec in self._files
            if spec.kind == kind
        ]

    @staticmethod
    def _runtime_files(root: Path) -> list[dict[str, object]]:
        result: list[dict[str, object]] = []
        for path in sorted(item for item in root.rglob("*") if item.is_file()):
            digest = hashlib.sha256()
            SenseVoiceComponentManager._update_digest(digest, path)
            result.append(
                {
                    "path": path.relative_to(root).as_posix(),
                    "size": path.stat().st_size,
                    "sha256": digest.hexdigest(),
                }
            )
        return result

    @staticmethod
    def _promote(staging: Path, destination: Path) -> None:
        destination.parent.mkdir(parents=True, exist_ok=True)
        backup = destination.with_name(destination.name + ".previous")
        shutil.rmtree(backup, ignore_errors=True)
        if destination.exists():
            destination.replace(backup)
        try:
            staging.replace(destination)
        except Exception:
            if backup.exists() and not destination.exists():
                backup.replace(destination)
            raise
        shutil.rmtree(backup, ignore_errors=True)

    @staticmethod
    def _write_json(path: Path, payload: object) -> None:
        path.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    @staticmethod
    def _valid_file(path: Path, spec: SpeechComponentFile) -> bool:
        if not path.is_file() or path.stat().st_size != spec.size:
            return False
        digest = hashlib.sha256()
        SenseVoiceComponentManager._update_digest(digest, path)
        return digest.hexdigest().lower() == spec.sha256.lower()

    @staticmethod
    def _update_digest(digest: _Digest, path: Path) -> None:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)

    @property
    def _download_size(self) -> int:
        return sum(item.size for item in self._files)

    @staticmethod
    def _directory_size(root: Path) -> int:
        if not root.exists():
            return 0
        return sum(path.stat().st_size for path in root.rglob("*") if path.is_file())


def _windows_trust_context() -> ssl.SSLContext:
    """Use normal TLS verification plus the Windows root store when available."""

    context = ssl.create_default_context()
    if sys.platform != "win32" or not hasattr(ssl, "enum_certificates"):
        return context
    try:
        for certificate, encoding, _trust in ssl.enum_certificates("ROOT"):
            if encoding == "x509_asn":
                context.load_verify_locations(
                    cadata=ssl.DER_cert_to_PEM_cert(certificate)
                )
    except (OSError, ssl.SSLError):
        # Keep the default CA set; TLS verification is never disabled.
        pass
    return context
