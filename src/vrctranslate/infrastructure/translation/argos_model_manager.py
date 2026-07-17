from __future__ import annotations

import importlib.util
import json
import os
from urllib.request import Request, urlopen
from pathlib import Path
from typing import Any

from vrctranslate.application.ports.local_models import LocalTranslationModel
from vrctranslate.domain.errors import TranslationError
from vrctranslate.infrastructure.paths import AppPaths


def _patch_sentence_boundary_detection() -> None:
    """用轻量标点分句替代 stanza/minisbd，避免离线时联网下载分句模型。

    Argos 默认用 stanza 做 NLP 级分句，首次使用会联网下载 resources.json，
    在无网络或证书异常时直接崩溃。VRChat 场景以短文本为主，标点分句足够。
    """
    import re

    try:
        from argostranslate import sbd
    except Exception:
        return  # 组件未安装，由 component_available 检查处理

    if getattr(sbd, "_vrctranslate_patched", False):
        return

    _SENTENCE_END = re.compile(r'(?<=[。！？!?\.…])\s*')

    def _simple_split(self, text: str):  # noqa: ANN001
        parts = _SENTENCE_END.split(text.strip())
        return [p for p in parts if p.strip()] or [text]

    sbd.StanzaSentencizer.split_sentences = _simple_split
    sbd.MiniSBDSentencizer.split_sentences = _simple_split
    sbd._vrctranslate_patched = True


class ArgosModelManager:
    """Manage Argos packages without importing Argos before paths are redirected."""

    def __init__(self, paths: AppPaths) -> None:
        self._paths = paths
        self.configure_environment()

    @property
    def component_available(self) -> bool:
        return importlib.util.find_spec("argostranslate") is not None

    @property
    def model_directory(self) -> str:
        return str(self._paths.argos_models_dir)

    def configure_environment(self) -> None:
        self._paths.ensure_writable()
        # Argos creates its default directories as soon as it is imported.
        # Every supported path is therefore redirected before any lazy import.
        values = {
            "XDG_DATA_HOME": self._paths.third_party_data_dir,
            "XDG_CONFIG_HOME": self._paths.third_party_config_dir,
            "XDG_CACHE_HOME": self._paths.third_party_cache_dir,
            "ARGOS_PACKAGES_DIR": self._paths.argos_models_dir,
            # 使用 jsdelivr CDN 镜像替代 GitHub raw，避免国内 502/超时
            "ARGOS_PACKAGE_INDEX": "https://cdn.jsdelivr.net/gh/argosopentech/argospm-index@main/",
            "ARGOS_DEVICE_TYPE": "cpu",
            "ARGOS_INTER_THREADS": "1",
            "ARGOS_INTRA_THREADS": "1",
            "ARGOS_BATCH_SIZE": "1",
            "ARGOS_BEAM_SIZE": "1",
            "ARGOS_COMPUTE_TYPE": "auto",
        }
        for name, value in values.items():
            os.environ[name] = str(value)
        _patch_sentence_boundary_detection()

    def installed_models(self) -> list[LocalTranslationModel]:
        package = self._package_module()
        return [self._to_model(item) for item in package.get_installed_packages()]

    def available_models(self, refresh: bool = False) -> list[LocalTranslationModel]:
        package = self._package_module()
        if refresh:
            before = self._index_fingerprint()
            self._download_index_with_fallback()
            after = self._index_fingerprint()
            if after is None or after == before:
                raise TranslationError(
                    "network",
                    "Argos 官方模型索引没有更新；请检查网络后重试。已有本地索引会继续保留。",
                )
        self._validate_local_index()
        try:
            packages = package.get_available_packages()
        except Exception as exc:
            raise TranslationError("network", "无法读取 Argos 语言模型列表") from exc
        return [self._to_model(item) for item in packages if getattr(item, "type", "translate") == "translate"]

    def install(
        self,
        source_language: str,
        target_language: str,
        package_version: str = "",
        progress_callback: Any = None,
    ) -> None:
        package = self._package_module()
        self._validate_local_index()
        candidate = next(
            (
                item
                for item in package.get_available_packages()
                if item.from_code == source_language
                and item.to_code == target_language
                and (
                    not package_version
                    or str(getattr(item, "package_version", "")) == package_version
                )
            ),
            None,
        )
        if candidate is None:
            raise TranslationError(
                "configuration",
                f"没有可用的 Argos 模型：{source_language} → {target_language}",
            )
        download_path: Path | None = None
        try:
            download_path = self._download_with_progress(candidate, progress_callback)
            package.install_from_path(download_path)
        except TranslationError:
            raise
        except Exception as exc:
            raise TranslationError("service", "Argos 模型下载或安装失败") from exc
        finally:
            if download_path is not None:
                download_path.unlink(missing_ok=True)

    def _download_with_progress(self, candidate: Any, progress_callback: Any = None) -> Path:
        """流式下载模型文件，通过回调报告进度。"""
        from argostranslate import settings as argos_settings

        package = self._package_module()
        filename = package.argospm_package_name(candidate) + ".argosmodel"
        filepath = Path(argos_settings.downloads_dir) / filename
        if filepath.exists():
            if progress_callback:
                progress_callback(1, 1)
            return filepath

        filepath.parent.mkdir(parents=True, exist_ok=True)
        links = getattr(candidate, "links", []) or []
        if not links:
            raise TranslationError("network", "模型下载链接为空")

        last_error: Exception | None = None
        for url in links:
            try:
                req = Request(url, headers={"User-Agent": "ArgosTranslate"})
                with urlopen(req, timeout=120) as response:
                    total = int(response.headers.get("Content-Length", 0))
                    downloaded = 0
                    with open(filepath, "wb") as f:
                        while True:
                            chunk = response.read(65536)
                            if not chunk:
                                break
                            f.write(chunk)
                            downloaded += len(chunk)
                            if progress_callback:
                                progress_callback(downloaded, total)
                return filepath
            except Exception as exc:
                last_error = exc
                filepath.unlink(missing_ok=True)
                continue

        raise TranslationError("network", f"模型下载失败：{last_error}")

    def remove(self, source_language: str, target_language: str) -> None:
        package = self._package_module()
        candidate = next(
            (
                item
                for item in package.get_installed_packages()
                if item.from_code == source_language and item.to_code == target_language
            ),
            None,
        )
        if candidate is None:
            raise TranslationError("configuration", "指定的 Argos 模型尚未安装")
        try:
            package.uninstall(candidate)
        except Exception as exc:
            raise TranslationError("service", "无法删除 Argos 模型") from exc

    def disk_usage(self) -> int:
        return sum(
            path.stat().st_size
            for path in self._paths.argos_models_dir.rglob("*")
            if path.is_file()
        )

    def translation_path(self, source: str, target: str) -> tuple[str, str]:
        """检查源语言到目标语言的翻译可用性。

        返回 (状态, 中转语言代码):
        - ('direct', ''): 已安装直接翻译模型
        - ('pivot', 'en'): 无直接模型，但可通过中间语言中转
        - ('unavailable', ''): 无法翻译
        """
        installed = self.installed_models()
        pairs = {(m.source_language, m.target_language) for m in installed}
        if (source, target) in pairs:
            return "direct", ""
        from_source = {m.target_language for m in installed if m.source_language == source}
        to_target = {m.source_language for m in installed if m.target_language == target}
        pivots = from_source & to_target
        if pivots:
            return "pivot", next(iter(pivots))
        return "unavailable", ""

    def _package_module(self) -> Any:
        self.configure_environment()
        if not self.component_available:
            raise TranslationError(
                "component",
                "当前版本未包含 Argos Translate 本地翻译组件",
            )
        try:
            from argostranslate import package, settings

            # Keep downloads in the application's explicit portable cache path.
            settings.downloads_dir = self._paths.downloads_dir
            settings.downloads_dir.mkdir(parents=True, exist_ok=True)
            return package
        except Exception as exc:
            raise TranslationError("component", "Argos Translate 组件加载失败") from exc

    def _validate_local_index(self) -> None:
        index_path = self._local_index_path()
        if index_path is None:
            raise TranslationError("index_missing", "无法确定 Argos 本地模型索引位置")
        if not index_path.is_file() or index_path.stat().st_size < 2:
            raise TranslationError(
                "index_missing",
                "便携 data 中没有 Argos 模型索引，需要联网刷新一次。",
            )
        try:
            payload = json.loads(index_path.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            raise TranslationError("index_missing", "Argos 本地模型索引损坏，请重新刷新。") from exc
        if not isinstance(payload, (list, dict)):
            raise TranslationError("index_missing", "Argos 本地模型索引格式无效。")

    def _local_index_path(self) -> Path | None:
        try:
            from argostranslate import settings

            return Path(settings.local_package_index)
        except Exception:
            return None

    def _index_fingerprint(self) -> tuple[int, int] | None:
        path = self._local_index_path()
        if path is None or not path.is_file():
            return None
        stat = path.stat()
        return stat.st_mtime_ns, stat.st_size

    _INDEX_MIRRORS = (
        "https://cdn.jsdelivr.net/gh/argosopentech/argospm-index@main/index.json",
        "https://fastly.jsdelivr.net/gh/argosopentech/argospm-index@main/index.json",
        "https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json",
    )

    def _download_index_with_fallback(self) -> None:
        """Download the package index from mirrors with timeout and fallback.

        Replaces argostranslate.package.update_package_index() which uses
        urllib without a timeout and silently swallows HTTP errors (causing
        the UI to hang on 502). Each mirror is tried with a 15s timeout.
        """
        index_path = self._local_index_path()
        if index_path is None:
            raise TranslationError("network", "无法确定 Argos 本地模型索引位置")
        last_error: Exception | None = None
        for url in self._INDEX_MIRRORS:
            try:
                with urlopen(url, timeout=15) as response:
                    data = response.read()
            except Exception as exc:
                last_error = exc
                continue
            index_path.parent.mkdir(parents=True, exist_ok=True)
            with open(index_path, "wb") as handle:
                handle.write(data)
            return
        raise TranslationError(
            "network",
            "Argos 官方模型索引更新失败；所有镜像均不可达，请检查网络后重试。",
        ) from last_error

    @staticmethod
    def _to_model(item: Any) -> LocalTranslationModel:
        return LocalTranslationModel(
            source_language=str(getattr(item, "from_code", "")),
            target_language=str(getattr(item, "to_code", "")),
            package_version=str(getattr(item, "package_version", "")),
        )
