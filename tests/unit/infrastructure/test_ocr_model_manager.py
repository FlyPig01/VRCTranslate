from __future__ import annotations

import hashlib

import httpx
import pytest

from vrctranslate.infrastructure.ocr.model_catalog import (
    OCR_MODEL_PACKAGES,
    OcrModelFile,
    OcrModelPackage,
)
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager


def test_catalog_includes_verified_portable_english_model() -> None:
    package = OCR_MODEL_PACKAGES["en"]
    recognition = package.files[-1]

    assert package.version == "PP-OCRv5-mobile"
    assert package.recognition_type == "mobile"
    assert recognition.relative_path == "en/recognition.onnx"
    assert recognition.size == 7_872_351
    assert recognition.sha256 == (
        "c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8"
    )


def _spec(path: str, content: bytes) -> OcrModelFile:
    return OcrModelFile(
        path,
        f"https://models.invalid/{path}",
        hashlib.sha256(content).hexdigest(),
        len(content),
    )


def test_models_install_atomically_inside_portable_directory(tmp_path) -> None:
    contents = {
        "shared/detection.onnx": b"detector",
        "shared/orientation.onnx": b"classifier",
        "zh/recognition.onnx": b"chinese-recognizer",
    }
    package = OcrModelPackage(
        "zh-CN",
        "test-model",
        "PP-OCRv5",
        "server",
        tuple(_spec(path, content) for path, content in contents.items()),
    )

    def respond(request: httpx.Request) -> httpx.Response:
        relative = request.url.path.lstrip("/")
        return httpx.Response(200, content=contents[relative])

    models = tmp_path / "data" / "models" / "ocr"
    cache = tmp_path / "data" / "cache" / "ocr-models"
    manager = OcrModelManager(
        models,
        cache,
        packages={"zh-CN": package},
        client_factory=lambda: httpx.Client(
            transport=httpx.MockTransport(respond)
        ),
    )
    progress: list[tuple[int, int]] = []

    status = manager.install("zh-CN", lambda done, total: progress.append((done, total)))

    assert status.installed
    assert status.exclusive_size == len(contents["zh/recognition.onnx"])
    assert status.required_download_size == 0
    assert manager.paths("zh-CN").recognition == models / "zh" / "recognition.onnx"
    assert all((models / path).read_bytes() == content for path, content in contents.items())
    assert manager.manifest_path.is_file()
    assert progress[-1] == (sum(map(len, contents.values())), sum(map(len, contents.values())))
    assert not list(cache.glob("*.part"))
    storage = manager.storage()
    assert storage.shared_size == len(contents["shared/detection.onnx"]) + len(
        contents["shared/orientation.onnx"]
    )
    assert storage.total_size == sum(map(len, contents.values()))

    manager.remove("zh-CN")
    assert not manager.status("zh-CN").installed
    assert not (models / "shared" / "detection.onnx").exists()


def test_corrupt_download_never_replaces_a_model(tmp_path) -> None:
    expected = b"expected"
    package = OcrModelPackage(
        "zh-CN",
        "test-model",
        "PP-OCRv5",
        "server",
        (_spec("zh/recognition.onnx", expected),),
    )
    manager = OcrModelManager(
        tmp_path / "models",
        tmp_path / "cache",
        packages={"zh-CN": package},
        client_factory=lambda: httpx.Client(
            transport=httpx.MockTransport(
                lambda _request: httpx.Response(200, content=b"corrupt")
            )
        ),
    )

    try:
        manager.install("zh-CN")
    except OSError:
        pass
    else:
        raise AssertionError("corrupt model was accepted")

    assert not (tmp_path / "models" / "zh" / "recognition.onnx").exists()
    assert not list((tmp_path / "cache").glob("*.part"))


def test_cancelled_progress_callback_cleans_partial_download(tmp_path) -> None:
    expected = b"model-content"
    package = OcrModelPackage(
        "zh-CN",
        "test-model",
        "PP-OCRv5",
        "server",
        (_spec("zh/recognition.onnx", expected),),
    )
    manager = OcrModelManager(
        tmp_path / "models",
        tmp_path / "cache",
        packages={"zh-CN": package},
        client_factory=lambda: httpx.Client(
            transport=httpx.MockTransport(
                lambda _request: httpx.Response(200, content=expected)
            )
        ),
    )

    def cancel(_completed: int, _total: int) -> None:
        raise RuntimeError("cancel")

    with pytest.raises(RuntimeError, match="cancel"):
        manager.install("zh-CN", cancel)

    assert not (tmp_path / "models" / "zh" / "recognition.onnx").exists()
    assert not list((tmp_path / "cache").glob("*.part"))
