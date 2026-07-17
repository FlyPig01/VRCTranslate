from __future__ import annotations

import ast
from pathlib import Path


PACKAGE_ROOT = Path(__file__).parents[2] / "src" / "vrctranslate"


def _imports(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    imported: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            imported.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            imported.add(node.module)
    return imported


def _python_files(directory: str) -> list[Path]:
    return list((PACKAGE_ROOT / directory).rglob("*.py"))


def test_package_root_contains_only_entrypoints_and_architecture_layers() -> None:
    allowed = {
        "__init__.py",
        "__main__.py",
        "bootstrap.py",
        "domain",
        "application",
        "infrastructure",
        "presentation",
    }
    actual = {
        path.name
        for path in PACKAGE_ROOT.iterdir()
        if path.name != "__pycache__"
    }
    assert actual == allowed


def test_domain_has_no_framework_or_infrastructure_imports() -> None:
    forbidden = ("PySide6", "httpx", "mss", "numpy", "pythonosc", "ctypes")
    for path in _python_files("domain"):
        imports = _imports(path)
        assert not any(name.startswith(forbidden) for name in imports), path
        assert not any(".infrastructure" in name for name in imports), path


def test_application_depends_only_inward() -> None:
    forbidden = ("PySide6", "httpx", "mss", "numpy", "pythonosc", "ctypes")
    for path in _python_files("application"):
        imports = _imports(path)
        assert not any(name.startswith(forbidden) for name in imports), path
        assert not any(
            marker in name
            for name in imports
            for marker in ("vrctranslate.infrastructure", "vrctranslate.presentation")
        ), path


def test_infrastructure_never_imports_presentation_or_qt() -> None:
    for path in _python_files("infrastructure"):
        imports = _imports(path)
        assert not any(name.startswith("PySide6") for name in imports), path
        assert not any("vrctranslate.presentation" in name for name in imports), path


def test_presentation_does_not_select_concrete_adapters() -> None:
    for path in _python_files("presentation"):
        if "controllers" in path.parts:
            continue
        imports = _imports(path)
        assert not any("vrctranslate.infrastructure" in name for name in imports), path


def test_capture_and_ocr_adapters_have_no_image_write_api() -> None:
    banned = ("imwrite", "imsave", "write_bytes", ".tofile(", "Image.save")
    paths = _python_files("infrastructure/capture") + _python_files(
        "infrastructure/ocr"
    )
    for path in paths:
        source = path.read_text(encoding="utf-8")
        assert not any(token in source for token in banned), path
