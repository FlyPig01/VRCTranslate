from __future__ import annotations

import sys
from collections.abc import Callable
from importlib.resources import files

from PySide6.QtWidgets import QApplication, QMainWindow, QMessageBox

from vrctranslate.presentation.qt.app_style import VrcTranslateStyle
from vrctranslate.presentation.qt.icon_resources import load_icon


def run_qt_application(window_factory: Callable[[], QMainWindow]) -> int:
    application = QApplication(sys.argv)
    application.setApplicationName("VRCTranslate")
    application.setOrganizationName("VRCTranslate")
    application.setWindowIcon(load_icon("app.ico"))
    application.setQuitOnLastWindowClosed(True)
    application.setStyle(VrcTranslateStyle(application.style()))
    styles = files("vrctranslate.presentation.qt").joinpath("resources", "styles")
    style_order = (
        "base.qss",
        "main_window.qss",
        "forms.qss",
        "tables.qss",
        "settings.qss",
        "quick_input.qss",
        "ocr_overlay.qss",
        "ocr_tools.qss",
        "voice_overlay.qss",
    )
    application.setStyleSheet(
        "\n".join(
            styles.joinpath(filename).read_text(encoding="utf-8")
            for filename in style_order
        )
    )
    try:
        window = window_factory()
    except OSError:
        QMessageBox.critical(
            None,
            "数据目录不可写",
            "VRCTranslate 无法写入软件目录下的 data。\n\n"
            "请把完整软件目录移动到 D/E 盘等普通可写目录后再运行。"
            "程序不会回退到 C 盘用户目录。",
        )
        return 2
    window.show()
    return application.exec()
