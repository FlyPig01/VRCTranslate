from __future__ import annotations

import logging

from PySide6.QtCore import Qt

from vrctranslate.application.dto import AppSettings, UiSettings
from vrctranslate.domain.ocr import CaptureRegion, WindowInfo
from vrctranslate.presentation.qt.controllers.ocr_controller import OcrController
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.windows.ocr_orb import OcrOrbWindow
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow
from vrctranslate.presentation.qt.windows.ocr_region import OcrRegionWindow


class _Signal:
    def connect(self, *_: object) -> None:
        return None


class _I18n:
    language_changed = _Signal()

    def tr(self, key: str, **kwargs: object) -> str:
        return key.format(**kwargs) if kwargs else key


class _Excluder:
    def __init__(self) -> None:
        self.handles: list[int] = []

    def exclude_from_capture(self, hwnd: int) -> bool:
        self.handles.append(hwnd)
        return True


def test_orb_uses_state_assets_and_click_toggles(qtbot) -> None:
    excluder = _Excluder()
    orb = OcrOrbWindow(excluder, _I18n())  # type: ignore[arg-type]
    qtbot.addWidget(orb)
    toggles: list[bool] = []
    orb.toggle_requested.connect(lambda: toggles.append(True))
    orb.apply_settings(UiSettings(ocr_orb_topmost=False))
    orb.show()
    qtbot.waitExposed(orb)

    for state in ("idle", "running", "waiting", "error"):
        orb.set_state(state)
        assert not orb.button.icon().isNull()
    assert not hasattr(orb, "target_action")
    assert all(action.text() != "ocr_orb.select_target" for action in orb.menu.actions())
    qtbot.mouseClick(orb, Qt.MouseButton.LeftButton)

    assert toggles == [True]
    assert excluder.handles


def test_region_frame_has_two_modes_and_close_removes_it(qtbot) -> None:
    region_window = OcrRegionWindow(i18n=_I18n())  # type: ignore[arg-type]
    qtbot.addWidget(region_window)
    target = WindowInfo(1, "VRChat", 100, 100, 800, 600, "VRChat.exe")
    region = CaptureRegion(40, 50, 300, 180)
    modes: list[str] = []
    closed: list[bool] = []
    region_window.mode_requested.connect(modes.append)
    region_window.close_requested.connect(lambda: closed.append(True))
    region_window.set_target(target, region)
    region_window.show()
    qtbot.waitExposed(region_window)
    assert not region_window.grab().isNull()

    qtbot.mouseClick(region_window.single_button, Qt.MouseButton.LeftButton)
    assert modes == ["single"]
    assert region_window.continuous_button.text() == "ocr_region.continuous"

    qtbot.mouseClick(region_window.close_button, Qt.MouseButton.LeftButton)
    assert closed == [True]
    assert region_window.isHidden()


def test_saved_region_is_not_shown_during_controller_startup(qtbot) -> None:
    target = WindowInfo(10, "VRChat", 100, 100, 800, 600, "VRChat.exe")
    other = WindowInfo(20, "Test Game", 200, 120, 900, 700, "game.exe")

    class Settings:
        location = "memory://settings"

        def __init__(self) -> None:
            self.current = AppSettings()
            self.save_count = 0
            self.current.ocr.region_x = 20
            self.current.ocr.region_y = 30
            self.current.ocr.region_width = 400
            self.current.ocr.region_height = 180

        def save(self, settings: AppSettings) -> None:
            self.current = settings
            self.save_count += 1

    class Capture:
        backend_name = "fake"
        semantics = "window_content"
        uses_screen_coordinates = False

        def set_mode(self, _mode: str) -> None:
            return

        def list_windows(self):
            return [target, other]

        def get_window(self, hwnd: int):
            return next((item for item in (target, other) if item.hwnd == hwnd), None)

    class Engine:
        def set_source_language(self, _language: str) -> None:
            return

    class Activator:
        def activate_window(self, _hwnd: int) -> bool:
            return True

    i18n = _I18n()
    page = OcrPage(i18n)  # type: ignore[arg-type]
    overlay = OcrOverlayWindow(i18n=i18n)  # type: ignore[arg-type]
    region = OcrRegionWindow(i18n=i18n)  # type: ignore[arg-type]
    orb = OcrOrbWindow(i18n=i18n)  # type: ignore[arg-type]
    for widget in (page, overlay, region, orb):
        qtbot.addWidget(widget)

    settings = Settings()
    controller = OcrController(
        page,
        overlay,
        region,
        orb,
        Capture(),  # type: ignore[arg-type]
        object(),  # type: ignore[arg-type]
        Engine(),  # type: ignore[arg-type]
        object(),  # type: ignore[arg-type]
        settings,  # type: ignore[arg-type]
        Activator(),  # type: ignore[arg-type]
        logging.getLogger("test-ocr-startup"),
        i18n,  # type: ignore[arg-type]
    )

    assert region.isHidden()
    assert orb.isVisible()
    assert page.selected_hwnd == target.hwnd

    page.target_combo.setCurrentIndex(page.target_combo.findData(other.hwnd))
    assert settings.current.ocr.window_title == other.title
    assert settings.current.ocr.region_width == 0
    assert region.isHidden()

    page.orb_topmost_check.setChecked(False)
    qtbot.wait(400)
    assert settings.current.ui.ocr_orb_topmost is False
    assert settings.save_count > 0

    controller._region_selected(CaptureRegion(40, 50, 320, 140))
    qtbot.wait(20)
    assert region.isVisible()
    assert not region.grab().isNull()
    assert controller.shutdown()
