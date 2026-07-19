from __future__ import annotations

import hashlib
import io
import shutil
import zipfile
from pathlib import Path
from types import SimpleNamespace

import pytest

from vrctranslate.application.dto import SpeechRecognitionProfile
from vrctranslate.application.ports.speech_models import SpeechModelPaths
from vrctranslate.domain.speech import SpeechRecognitionRequest
from vrctranslate.infrastructure.speech.local_component_catalog import SpeechComponentFile
from vrctranslate.infrastructure.speech.local_component_manager import (
    SenseVoiceComponentManager,
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

    def stream(self, _method: str, url: str, *, headers=None):
        del headers
        return _Response(self.payloads[url])


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
    @staticmethod
    def from_sense_voice(**kwargs):
        assert kwargs["language"] == "auto"
        assert kwargs["provider"] == "cpu"
        return _Recognizer()


class _Adapter(SenseVoiceLocalSpeechRecognizer):
    def _load_runtime(self, root: Path):
        del root
        return SimpleNamespace(OfflineRecognizer=_Factory)


def test_sensevoice_adapter_normalizes_language_and_vrchat_terms(tmp_path: Path) -> None:
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


def test_sensevoice_normalization_repairs_conservative_multilingual_domain_terms() -> None:
    normalize = SenseVoiceLocalSpeechRecognizer._normalize_text

    assert normalize("这个VR chatt世界使用U荡脚本") == "这个VRChat世界使用Udon脚本"
    assert normalize("この アルチャット ワールド は うどん スクリプト") == (
        "この VRChat ワールド は Udon スクリプト"
    )
    assert normalize("我的fallllback avatar") == "我的Fallback Avatar"
