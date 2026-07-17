from __future__ import annotations

import hashlib

import httpx

from vrctranslate.infrastructure.ocr.model_catalog import (
    OcrModelFile,
    OcrModelPackage,
)
from vrctranslate.infrastructure.ocr.model_manager import OcrModelManager


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
    assert manager.paths("zh-CN").recognition == models / "zh" / "recognition.onnx"
    assert all((models / path).read_bytes() == content for path, content in contents.items())
    assert manager.manifest_path.is_file()
    assert progress[-1] == (sum(map(len, contents.values())), sum(map(len, contents.values())))
    assert not list(cache.glob("*.part"))

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
