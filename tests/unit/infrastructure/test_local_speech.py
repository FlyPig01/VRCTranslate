from __future__ import annotations

import hashlib
import io
import shutil
import zipfile
from pathlib import Path
from time import sleep
from types import SimpleNamespace

import pytest

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.application.ports.speech_models import SpeechModelPaths
from vrctranslate.domain.speech import SpeechRecognitionRequest
from vrctranslate.infrastructure.speech.local_component_catalog import (
    SPEECH_COMPONENT_FILES,
    SpeechComponentFile,
)
from vrctranslate.infrastructure.speech.local_component_manager import (
    SenseVoiceComponentManager,
)
from vrctranslate.infrastructure.speech.download_source_selector import (
    AdaptiveDownloadSourceSelector,
)
from vrctranslate.infrastructure.speech.sensevoice_local import (
    SenseVoiceLocalSpeechRecognizer,
)


def _wheel_bytes() -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr("sherpa_onnx/__init__.py", "")
        archive.writestr("sherpa_onnx/lib/_sherpa_onnx.cp311-win_amd64.pyd", b"pyd")
        archive.writestr("sherpa_onnx/lib/onnxruntime.dll", b"ort")
        archive.writestr("sherpa_onnx/lib/sherpa-onnx-c-api.dll", b"api")
        archive.writestr("sherpa_onnx/include/not-needed.h", b"header")
        archive.writestr("sherpa_onnx/lib/not-needed.lib", b"library")
    return output.getvalue()


def _spec(kind: str, path: str, payload: bytes, *, wheel: bool = False):
    return SpeechComponentFile(
        kind,  # type: ignore[arg-type]
        path,
        f"memory://{path}",
        hashlib.sha256(payload).hexdigest(),
        len(payload),
        wheel,
    )


def test_sensevoice_model_download_declares_mirror_then_official_fallback() -> None:
    model_files = [item for item in SPEECH_COMPONENT_FILES if item.kind == "model"]

    assert model_files
    assert all(item.url.startswith("https://hf-mirror.com/") for item in model_files)
    assert all(
        item.fallback_urls
        and item.fallback_urls[0].startswith("https://huggingface.co/")
        for item in model_files
    )


class _Response:
    status_code = 200

    def __init__(self, payload: bytes) -> None:
        self.payload = payload

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def raise_for_status(self) -> None:
        pass

    def iter_bytes(self, _size: int):
        yield self.payload


class _Client:
    def __init__(self, payloads: dict[str, bytes]) -> None:
        self.payloads = payloads

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def stream(self, _method: str, url: str, *, headers=None, timeout=None):
        del headers
        del timeout
        return _Response(self.payloads[url])


class _DelayedResponse(_Response):
    def __init__(self, payload: bytes, delay: float) -> None:
        super().__init__(payload)
        self.delay = delay

    def iter_bytes(self, _size: int):
        sleep(self.delay)
        yield self.payload


class _TimedClient:
    def __init__(self, payload: bytes, delays: dict[str, float]) -> None:
        self.payload = payload
        self.delays = delays
        self.requests: list[tuple[str, dict[str, str] | None]] = []

    def stream(self, _method: str, url: str, *, headers=None, timeout=None):
        del timeout
        self.requests.append((url, headers))
        return _DelayedResponse(self.payload, self.delays[url])


class _PartlyUnavailableClient(_TimedClient):
    def __init__(
        self,
        payload: bytes,
        delays: dict[str, float],
        unavailable_url: str,
    ) -> None:
        super().__init__(payload, delays)
        self.unavailable_url = unavailable_url

    def stream(self, _method: str, url: str, *, headers=None, timeout=None):
        if url == self.unavailable_url:
            raise OSError("source unavailable")
        return super().stream(
            _method,
            url,
            headers=headers,
            timeout=timeout,
        )


def test_download_source_selector_measures_real_transfer_and_reuses_host_order() -> None:
    payload = b"x" * (64 * 1024)
    official = "https://huggingface.co/example/model.bin"
    mirror = "https://hf-mirror.com/example/model.bin"
    client = _TimedClient(
        payload,
        {
            official: 0.04,
            mirror: 0.005,
        },
    )
    selector = AdaptiveDownloadSourceSelector(
        probe_bytes=len(payload),
        probe_timeout_seconds=1.0,
    )

    ordered = selector.order(  # type: ignore[arg-type]
        client,
        (official, mirror),
    )

    assert ordered == (mirror, official)
    assert len(client.requests) == 2
    assert all(
        request_headers == {"Range": f"bytes=0-{len(payload) - 1}"}
        for _url, request_headers in client.requests
    )

    token_urls = (
        "https://huggingface.co/example/tokens.txt",
        "https://hf-mirror.com/example/tokens.txt",
    )
    assert selector.order(  # type: ignore[arg-type]
        client,
        token_urls,
    ) == tuple(reversed(token_urls))
    assert len(client.requests) == 2


def test_download_source_selector_keeps_unavailable_source_as_fallback() -> None:
    payload = b"x" * (64 * 1024)
    unavailable = "https://primary.example/model.bin"
    available = "https://backup.example/model.bin"
    client = _PartlyUnavailableClient(
        payload,
        {available: 0.001},
        unavailable,
    )
    selector = AdaptiveDownloadSourceSelector(
        probe_bytes=len(payload),
        probe_timeout_seconds=1.0,
    )

    assert selector.order(  # type: ignore[arg-type]
        client,
        (unavailable, available),
    ) == (available, unavailable)


def test_component_manager_downloads_from_the_measured_faster_source(
    tmp_path: Path,
) -> None:
    payload = b"m" * (64 * 1024)
    slower = "https://slower.example/model.bin"
    faster = "https://faster.example/model.bin"
    spec = SpeechComponentFile(
        "model",
        "model.bin",
        slower,
        hashlib.sha256(payload).hexdigest(),
        len(payload),
        fallback_urls=(faster,),
    )
    client = _TimedClient(
        payload,
        {
            slower: 0.04,
            faster: 0.005,
        },
    )
    selector = AdaptiveDownloadSourceSelector(
        probe_bytes=len(payload),
        probe_timeout_seconds=1.0,
    )
    manager = SenseVoiceComponentManager(
        tmp_path / "models",
        tmp_path / "runtime",
        tmp_path / "cache",
        files=(spec,),
        source_selector=selector,
    )
    manager.cache_root.mkdir(parents=True)

    downloaded = manager._download(  # type: ignore[arg-type]
        client,
        spec,
        0,
        len(payload),
        None,
    )

    assert downloaded.read_bytes() == payload
    assert client.requests[-1] == (faster, None)


def test_component_manager_installs_verified_portable_runtime(tmp_path: Path) -> None:
    model = b"model"
    tokens = b"tokens"
    wheel = _wheel_bytes()
    files = (
        _spec("model", "model.int8.onnx", model),
        _spec("model", "tokens.txt", tokens),
        _spec("runtime", "runtime.whl", wheel, wheel=True),
    )
    payloads = {item.url: value for item, value in zip(files, (model, tokens, wheel))}
    manager = SenseVoiceComponentManager(
        tmp_path / "models",
        tmp_path / "runtime",
        tmp_path / "cache",
        files=files,
        client_factory=lambda: _Client(payloads),  # type: ignore[arg-type]
    )

    before = manager.status()
    assert not before.installed
    assert before.download_size == sum(item.size for item in files)

    installed = manager.install()

    assert installed.installed
    assert manager.verify().model.read_bytes() == model
    assert not (tmp_path / "runtime/sherpa_onnx/include").exists()
    assert not (tmp_path / "runtime/sherpa_onnx/lib/not-needed.lib").exists()
    native = tmp_path / "runtime/sherpa_onnx/lib/onnxruntime.dll"
    native.write_bytes(b"bad")
    with pytest.raises(OSError, match="运行库校验失败"):
        manager.verify()
    manager.remove()
    assert not manager.status().installed


def test_runtime_wheel_rejects_path_traversal(tmp_path: Path) -> None:
    wheel = tmp_path / "bad.whl"
    with zipfile.ZipFile(wheel, "w") as archive:
        archive.writestr("../outside.dll", b"bad")

    with pytest.raises(OSError, match="非法路径"):
        SenseVoiceComponentManager._extract_runtime_wheel(wheel, tmp_path / "out")


def test_pending_native_runtime_removal_finishes_on_next_start(tmp_path: Path) -> None:
    models = tmp_path / "models"
    runtime = tmp_path / "components" / "runtime"
    cache = tmp_path / "cache"
    models.mkdir(parents=True)
    runtime.mkdir(parents=True)
    (models / "held-model").write_bytes(b"model")
    (runtime / "held-runtime").write_bytes(b"runtime")
    marker = runtime.with_name(runtime.name + ".remove-pending")
    marker.write_text("pending", encoding="utf-8")

    SenseVoiceComponentManager(models, runtime, cache, files=())

    assert not models.exists()
    assert not runtime.exists()
    assert not marker.exists()


def test_component_install_rejects_insufficient_portable_disk_space(
    tmp_path: Path, monkeypatch
) -> None:
    manager = SenseVoiceComponentManager(
        tmp_path / "models",
        tmp_path / "runtime",
        tmp_path / "cache",
        files=(),
    )
    monkeypatch.setattr(
        shutil,
        "disk_usage",
        lambda _path: SimpleNamespace(total=1, used=0, free=1),
    )

    with pytest.raises(OSError, match="安装空间不足"):
        manager.install()


class _Components:
    def __init__(self, root: Path) -> None:
        self.verify_calls = 0
        root.mkdir(parents=True, exist_ok=True)
        (root / "model.int8.onnx").write_bytes(b"model")
        (root / "tokens.txt").write_text("tokens", encoding="utf-8")
        (root / "runtime").mkdir(exist_ok=True)
        self.paths = SpeechModelPaths(
            root / "model.int8.onnx",
            root / "tokens.txt",
            root / "runtime",
        )

    def verify(self):
        self.verify_calls += 1
        return self.paths


class _Result:
    text = "<|en|><|NEUTRAL|> VR chat and physical bones"
    lang = "<|en|>"


class _Stream:
    result = _Result()

    def accept_waveform(self, sample_rate, samples):
        assert sample_rate == 16_000
        assert samples.dtype.name == "float32"


class _Recognizer:
    def create_stream(self):
        return _Stream()

    def decode_stream(self, stream):
        assert isinstance(stream, _Stream)


class _Factory:
    languages: list[str] = []

    @staticmethod
    def from_sense_voice(**kwargs):
        _Factory.languages.append(kwargs["language"])
        assert kwargs["provider"] == "cpu"
        return _Recognizer()


class _Adapter(SenseVoiceLocalSpeechRecognizer):
    def _load_runtime(self, root: Path):
        del root
        return SimpleNamespace(OfflineRecognizer=_Factory)


def test_sensevoice_adapter_normalizes_language_and_vrchat_terms(tmp_path: Path) -> None:
    _Factory.languages = []
    components = _Components(tmp_path)
    adapter = _Adapter(components)  # type: ignore[arg-type]
    profile = SpeechRecognitionProfile(
        provider="local_offline", model="sensevoice-small-int8"
    )

    result = adapter.transcribe(
        SpeechRecognitionRequest("request", b"\x01\x00" * 1600, 16_000, "auto"),
        profile,
    )

    assert result.detected_language == "en"
    assert result.text == "VRChat and PhysBone"
    adapter.transcribe(
        SpeechRecognitionRequest("request-2", b"\x01\x00" * 1600, 16_000, "auto"),
        profile,
    )
    assert components.verify_calls == 1
    assert _Factory.languages == ["auto"]


def test_sensevoice_adapter_applies_explicit_supported_language(tmp_path: Path) -> None:
    _Factory.languages = []
    components = _Components(tmp_path)
    adapter = _Adapter(components)  # type: ignore[arg-type]
    profile = SpeechRecognitionProfile(
        provider="local_offline", model="sensevoice-small-int8"
    )
    pcm = b"\x01\x00" * 1600

    japanese = adapter.transcribe(
        SpeechRecognitionRequest("ja-1", pcm, 16_000, "ja"),
        profile,
    )
    adapter.transcribe(
        SpeechRecognitionRequest("ja-2", pcm, 16_000, "ja"),
        profile,
    )
    adapter.transcribe(
        SpeechRecognitionRequest("en-1", pcm, 16_000, "en"),
        profile,
    )

    assert japanese.detected_language == "en"
    assert _Factory.languages == ["ja", "en"]
    assert components.verify_calls == 1


def test_sensevoice_normalization_repairs_conservative_multilingual_domain_terms() -> None:
    normalize = SenseVoiceLocalSpeechRecognizer._normalize_text

    assert normalize("这个VR chatt世界使用U荡脚本") == "这个VRChat世界使用Udon脚本"
    assert normalize("この アルチャット ワールド は うどん スクリプト") == (
        "この VRChat ワールド は Udon スクリプト"
    )
    assert normalize("我的fallllback avatar") == "我的Fallback Avatar"
