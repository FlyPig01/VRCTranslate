# -*- mode: python ; coding: utf-8 -*-
from __future__ import annotations

import os
import shutil
from pathlib import Path

from PyInstaller.utils.hooks import collect_all, collect_data_files


ROOT = Path(SPECPATH)
debug_console = os.environ.get("VRC_TRANSLATE_BUILD_CONSOLE") == "1"
product_name = (
    "VRCTranslate-Debug"
    if debug_console
    else "VRCTranslate"
)

datas = []
binaries = []
hiddenimports = []
excludes = [
    # ── Unused PySide6 Qt modules ──
    "PySide6.QtWebEngineCore",
    "PySide6.QtWebEngineWidgets",
    "PySide6.QtWebEngine",
    "PySide6.QtWebChannel",
    "PySide6.QtMultimedia",
    "PySide6.QtMultimediaWidgets",
    "PySide6.QtSpatialAudio",
    "PySide6.Qt3DCore",
    "PySide6.Qt3DRender",
    "PySide6.Qt3DInput",
    "PySide6.Qt3DLogic",
    "PySide6.Qt3DAnimation",
    "PySide6.Qt3DExtras",
    "PySide6.QtCharts",
    "PySide6.QtDataVisualization",
    "PySide6.QtQml",
    "PySide6.QtQuick",
    "PySide6.QtQuickControls2",
    "PySide6.QtQuickWidgets",
    "PySide6.QtQuick3D",
    "PySide6.QtSql",
    "PySide6.QtTest",
    "PySide6.QtHelp",
    "PySide6.QtBluetooth",
    "PySide6.QtNfc",
    "PySide6.QtPositioning",
    "PySide6.QtLocation",
    "PySide6.QtSensors",
    "PySide6.QtSerialPort",
    "PySide6.QtSerialBus",
    "PySide6.QtTextToSpeech",
    "PySide6.QtAxContainer",
    "PySide6.QtDesigner",
    "PySide6.QtUiTools",
    "PySide6.QtStateMachine",
    "PySide6.QtConcurrent",
    "PySide6.QtPdf",
    "PySide6.QtPdfWidgets",
    "PySide6.QtNetworkAuth",
    "PySide6.QtRemoteObjects",
    "PySide6.QtScxml",
    "PySide6.QtWebSockets",
    "PySide6.QtHttpServer",
    "PySide6.QtLabsAnimation",
    "PySide6.QtLabsFolderListModel",
    "PySide6.QtLabsQmlModels",
    "PySide6.QtLabsSettings",
    "PySide6.QtLabsSharedImage",
    "PySide6.QtLabsWavefrontMesh",
    "torch",
    "paddle",
    "paddleocr",
    "openvino",
    "tensorrt",
    "MNN",
]

datas += collect_data_files(
    "vrctranslate.presentation.qt",
    includes=["resources/**/*", "i18n/locales/*.json"],
)
datas += collect_data_files(
    "vrctranslate.infrastructure.glossary.resources",
    includes=["default_glossary.json"],
)

# OCR models and package resources are loaded dynamically at runtime.
for package_name in (
    "rapidocr",
    "alibabacloud_alimt20181012",
    "alibabacloud_tea_openapi",
    "alibabacloud_tea_util",
    "Tea",
):
    package_datas, package_binaries, package_hidden = collect_all(package_name)
    # High-accuracy OCR models are installed lazily into portable data/models/ocr.
    if package_name == "rapidocr":
        package_datas = [
            item
            for item in package_datas
            if not str(item[0]).lower().endswith(".onnx")
        ]
    datas += package_datas
    binaries += package_binaries
    hiddenimports += package_hidden

a = Analysis(
    [str(ROOT / "src" / "vrctranslate" / "__main__.py")],
    pathex=[str(ROOT / "src")],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=excludes,
    noarchive=False,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name=product_name,
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=debug_console,
    icon=str(ROOT / "src" / "vrctranslate" / "presentation" / "qt" / "resources" / "icons" / "app.ico"),
    contents_directory="_internal",
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    name=product_name,
)

# PyInstaller 6 puts bundled data under _internal. User-facing documents and
# the writable data directory must instead sit beside the executable.
release_root = Path(DISTPATH) / product_name
(release_root / "data").mkdir(parents=True, exist_ok=True)
shutil.copy2(ROOT / "README.md", release_root / "README.md")
shutil.copy2(ROOT / "使用说明.md", release_root / "使用说明.md")
shutil.copy2(
    ROOT / "多语言翻译质量测试报告.md",
    release_root / "多语言翻译质量测试报告.md",
)
shutil.copy2(
    ROOT / "多语言全链路质量与性能测试报告.md",
    release_root / "多语言全链路质量与性能测试报告.md",
)
shutil.copy2(
    ROOT / "THIRD_PARTY_NOTICES.md",
    release_root / "THIRD_PARTY_NOTICES.md",
)
shutil.copy2(
    ROOT / "packaging" / "portable-data-readme.txt",
    release_root / "data" / "README.txt",
)
