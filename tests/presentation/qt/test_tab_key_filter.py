from __future__ import annotations

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QApplication, QLineEdit, QVBoxLayout, QWidget

from vrctranslate.presentation.qt.tab_key_filter import TabKeyFilter


def _two_field_window(*, tool: bool = False) -> tuple[QWidget, QLineEdit, QLineEdit]:
    window = QWidget()
    if tool:
        window.setWindowFlag(Qt.WindowType.Tool, True)
    layout = QVBoxLayout(window)
    first = QLineEdit()
    second = QLineEdit()
    layout.addWidget(first)
    layout.addWidget(second)
    return window, first, second


def test_tab_and_backtab_are_blocked_in_main_and_floating_windows(qtbot) -> None:
    application = QApplication.instance()
    assert application is not None
    key_filter = TabKeyFilter(application)
    application.installEventFilter(key_filter)
    main, main_first, _main_second = _two_field_window()
    overlay, overlay_first, _overlay_second = _two_field_window(tool=True)
    qtbot.addWidget(main)
    qtbot.addWidget(overlay)
    try:
        main.show()
        qtbot.waitExposed(main)
        main_first.setFocus()
        qtbot.keyPress(main_first, Qt.Key.Key_Tab)
        qtbot.keyPress(main_first, Qt.Key.Key_Backtab)
        assert main.focusWidget() is main_first

        overlay.show()
        qtbot.waitExposed(overlay)
        overlay_first.setFocus()
        qtbot.keyPress(overlay_first, Qt.Key.Key_Tab)
        qtbot.keyPress(overlay_first, Qt.Key.Key_Backtab)
        assert overlay.focusWidget() is overlay_first
    finally:
        application.removeEventFilter(key_filter)
