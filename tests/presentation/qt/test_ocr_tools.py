from __future__ import annotations

import logging

from PySide6.QtCore import QAbstractAnimation, QPoint, QSize, Qt
from PySide6.QtGui import QGuiApplication

from vrctranslate.application.dto import AppSettings, UiSettings
from vrctranslate.domain.ocr import CaptureRegion, WindowInfo
from vrctranslate.presentation.qt.controllers.ocr_controller import OcrController
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.windows.ocr_orb import OcrOrbWindow
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow
from vrctranslate.presentation.qt.windows.ocr_region import OcrRegionWindow
from vrctranslate.presentation.qt.windows.voice_overlay_window import VoiceOverlayWindow


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


def test_orb_uses_state_assets_and_left_click_menu(qtbot) -> None:
    excluder = _Excluder()
    orb = OcrOrbWindow(excluder, _I18n())  # type: ignore[arg-type]
    qtbot.addWidget(orb)
    toggles: list[bool] = []
    overlay_hides: list[bool] = []
    orb.toggle_requested.connect(lambda: toggles.append(True))
    orb.overlay_hide_requested.connect(lambda: overlay_hides.append(True))
    orb.apply_settings(UiSettings(ocr_orb_topmost=False))
    orb.show()
    qtbot.waitExposed(orb)

    for state in ("idle", "running", "waiting", "error"):
        orb.set_state(state)
        assert not orb.button.icon().isNull()
    display_modes: list[str] = []
    orb.display_mode_requested.connect(display_modes.append)
    orb.display_actions["inline"].trigger()
    assert display_modes == ["inline"]
    assert orb.display_actions["inline"].isChecked()
    assert not hasattr(orb, "target_action")
    assert all(action.text() != "ocr_orb.select_target" for action in orb.menu.actions())
    qtbot.mouseClick(orb, Qt.MouseButton.LeftButton)
    qtbot.waitUntil(orb.menu.isVisible, timeout=1000)

    assert toggles == []
    assert (
        orb.menu._visibility_animation.state()
        == QAbstractAnimation.State.Running
    )
    assert (
        orb.menu._visibility_animation.startValue().height()
        < orb.menu._visibility_animation.endValue().height()
    )
    qtbot.waitUntil(
        lambda: orb.menu._visibility_animation.state()
        == QAbstractAnimation.State.Stopped,
        timeout=1000,
    )
    qtbot.mouseClick(orb, Qt.MouseButton.LeftButton)
    assert (
        orb.menu._visibility_animation.state()
        == QAbstractAnimation.State.Running
    )
    qtbot.waitUntil(lambda: not orb.menu.isVisible(), timeout=1000)

    qtbot.mouseClick(orb, Qt.MouseButton.LeftButton)
    qtbot.waitUntil(
        lambda: orb.menu._visibility_animation.state()
        == QAbstractAnimation.State.Stopped,
        timeout=1000,
    )
    qtbot.mouseClick(orb, Qt.MouseButton.RightButton)
    assert (
        orb.menu._visibility_animation.state()
        == QAbstractAnimation.State.Running
    )
    assert (
        orb.menu._visibility_animation.startValue().height()
        > orb.menu._visibility_animation.endValue().height()
    )
    qtbot.waitUntil(lambda: not orb.menu.isVisible(), timeout=1000)

    qtbot.mouseClick(orb, Qt.MouseButton.LeftButton)
    qtbot.waitUntil(
        lambda: orb.menu._visibility_animation.state()
        == QAbstractAnimation.State.Stopped,
        timeout=1000,
    )
    qtbot.mouseClick(
        orb.menu,
        Qt.MouseButton.LeftButton,
        pos=orb.menu.actionGeometry(orb.pause_action).center(),
    )
    assert toggles == [True]
    assert (
        orb.menu._visibility_animation.state()
        == QAbstractAnimation.State.Running
    )
    qtbot.waitUntil(lambda: not orb.menu.isVisible(), timeout=1000)
    assert not orb.menu.isVisible()
    assert excluder.handles
    orb.hide_overlay_action.trigger()
    assert overlay_hides == [True]


def test_ocr_and_voice_orbs_use_the_same_outer_size(qtbot) -> None:
    ocr = OcrOrbWindow(i18n=_I18n())  # type: ignore[arg-type]
    voice = VoiceOverlayWindow(i18n=_I18n())  # type: ignore[arg-type]
    qtbot.addWidget(ocr)
    qtbot.addWidget(voice)

    assert ocr.size() == voice.size() == QSize(58, 58)
    assert ocr.button.iconSize() == voice.orb_button.iconSize() == QSize(48, 48)


def test_region_frame_has_two_modes_and_close_removes_it(qtbot) -> None:
    region_window = OcrRegionWindow(i18n=_I18n())  # type: ignore[arg-type]
    qtbot.addWidget(region_window)
    target = WindowInfo(1, "VRChat", 100, 100, 800, 600, "VRChat.exe")
    region = CaptureRegion(40, 50, 300, 180)
    modes: list[str] = []
    closed: list[bool] = []
    interactions: list[bool] = []
    region_window.mode_requested.connect(modes.append)
    region_window.close_requested.connect(lambda: closed.append(True))
    region_window.interaction_started.connect(lambda: interactions.append(True))
    region_window.set_target(target, region)
    region_window.show()
    qtbot.waitExposed(region_window)
    assert not region_window.grab().isNull()

    qtbot.mouseClick(region_window.single_button, Qt.MouseButton.LeftButton)
    assert modes == ["single"]
    assert interactions == []
    display_modes: list[str] = []
    region_window.display_mode_requested.connect(display_modes.append)
    region_window.display_actions["both"].trigger()
    assert display_modes == ["both"]
    assert region_window.display_actions["both"].isChecked()
    assert region_window.continuous_button.text() == "ocr_region.continuous"

    qtbot.mouseClick(region_window.close_button, Qt.MouseButton.LeftButton)
    assert closed == [True]
    assert region_window.isHidden()


def test_ocr_floating_tools_expose_the_error_reason(qtbot) -> None:
    message = "翻译服务认证失败，请检查 API 密钥"
    region = OcrRegionWindow(i18n=_I18n())  # type: ignore[arg-type]
    orb = OcrOrbWindow(i18n=_I18n())  # type: ignore[arg-type]
    qtbot.addWidget(region)
    qtbot.addWidget(orb)

    region.set_error(message)
    orb.set_error(message)

    assert message in region.state_label.text()
    assert region.state_label.toolTip() == message
    assert message in orb.toolTip()


def test_region_content_does_not_accidentally_adjust_selection(qtbot) -> None:
    region_window = OcrRegionWindow(i18n=_I18n())  # type: ignore[arg-type]
    qtbot.addWidget(region_window)
    region_window.set_target(
        WindowInfo(1, "VRChat", 100, 100, 800, 600, "VRChat.exe"),
        CaptureRegion(40, 50, 300, 180),
    )
    region_window.show()
    qtbot.waitExposed(region_window)
    original = region_window.geometry()
    interactions: list[bool] = []
    region_window.interaction_started.connect(lambda: interactions.append(True))
    content_point = region_window.rect().center()
    content_point.setY(OcrRegionWindow.BAR_HEIGHT + 40)

    qtbot.mousePress(region_window, Qt.MouseButton.LeftButton, pos=content_point)
    qtbot.mouseMove(region_window, content_point + QPoint(40, 30))
    qtbot.mouseRelease(region_window, Qt.MouseButton.LeftButton, pos=content_point + QPoint(40, 30))

    assert interactions == []
    assert region_window.geometry() == original

    region_window.setCursor(Qt.CursorShape.SizeHorCursor)
    qtbot.mouseMove(region_window, region_window.bar.geometry().center())
    assert region_window.bar.cursor().shape() == Qt.CursorShape.ArrowCursor
    assert (
        region_window.single_button.cursor().shape()
        == Qt.CursorShape.PointingHandCursor
    )


def test_region_is_locked_while_ocr_is_running(qtbot) -> None:
    region_window = OcrRegionWindow(i18n=_I18n())  # type: ignore[arg-type]
    qtbot.addWidget(region_window)
    region_window.set_target(
        WindowInfo(1, "VRChat", 100, 100, 800, 600, "VRChat.exe"),
        CaptureRegion(40, 50, 300, 180),
    )
    region_window.set_state("running")
    region_window.show()
    qtbot.waitExposed(region_window)
    original = region_window.geometry()
    interactions: list[bool] = []
    region_window.interaction_started.connect(lambda: interactions.append(True))
    border_point = QPoint(2, region_window.height() // 2)

    qtbot.mousePress(region_window, Qt.MouseButton.LeftButton, pos=border_point)
    qtbot.mouseMove(region_window, border_point + QPoint(35, 0))
    qtbot.mouseRelease(region_window, Qt.MouseButton.LeftButton, pos=border_point + QPoint(35, 0))

    assert interactions == []
    assert region_window.geometry() == original


def test_region_controls_move_inside_at_the_screen_top(qtbot) -> None:
    screen = QGuiApplication.primaryScreen()
    assert screen is not None
    area = screen.availableGeometry()
    region_window = OcrRegionWindow(i18n=_I18n())  # type: ignore[arg-type]
    qtbot.addWidget(region_window)
    selected = CaptureRegion(0, 0, 360, 180)
    region_window.set_target(
        WindowInfo(
            1,
            "VRChat",
            area.left() + 80,
            area.top(),
            900,
            600,
            "VRChat.exe",
        ),
        selected,
    )
    region_window.show()
    qtbot.waitExposed(region_window)

    assert region_window._bar_inside is True
    assert region_window.y() >= area.top()
    assert region_window.bar.y() == 0
    assert region_window.current_region() == selected


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

    overlay.show()
    assert overlay.isVisible()
    orb.hide_overlay_action.trigger()
    assert overlay.isHidden()
    assert region.isVisible()

    orb.display_actions["inline"].trigger()
    assert settings.current.ui.ocr_display_mode == "inline"
    assert page.display_mode_combo.currentData() == "inline"
    assert region.display_actions["inline"].isChecked()

    region.display_actions["both"].trigger()
    assert settings.current.ui.ocr_display_mode == "both"
    assert page.display_mode_combo.currentData() == "both"
    assert orb.display_actions["both"].isChecked()

    page.display_mode_combo.setCurrentIndex(
        page.display_mode_combo.findData("overlay")
    )
    qtbot.wait(400)
    assert settings.current.ui.ocr_display_mode == "overlay"
    assert orb.display_actions["overlay"].isChecked()
    assert region.display_actions["overlay"].isChecked()
    assert controller.shutdown()


def test_mss_mode_uses_desktop_target_without_process_selection(qtbot) -> None:
    desktop = WindowInfo(0, "Desktop", 0, 0, 1920, 1080)

    class Settings:
        location = "memory://settings"

        def __init__(self) -> None:
            self.current = AppSettings()
            self.current.ocr.capture_backend = "screen"
            self.current.ocr.region_width = 640
            self.current.ocr.region_height = 360

        def save(self, settings: AppSettings) -> None:
            self.current = settings

    class Capture:
        backend_name = "MSS"
        semantics = "screen_coordinates"
        uses_screen_coordinates = True

        def set_mode(self, mode: str) -> None:
            assert mode == "screen"

        def screen_target(self):
            return desktop

        def list_windows(self):
            raise AssertionError("MSS mode must not enumerate target processes")

        def get_window(self, hwnd: int):
            return desktop if hwnd == 0 else None

    class Engine:
        def set_source_language(self, _language: str) -> None:
            return

    class Activator:
        def __init__(self) -> None:
            self.handles: list[int] = []

        def activate_window(self, hwnd: int) -> bool:
            self.handles.append(hwnd)
            return True

    i18n = _I18n()
    page = OcrPage(i18n)  # type: ignore[arg-type]
    overlay = OcrOverlayWindow(i18n=i18n)  # type: ignore[arg-type]
    region = OcrRegionWindow(i18n=i18n)  # type: ignore[arg-type]
    orb = OcrOrbWindow(i18n=i18n)  # type: ignore[arg-type]
    for widget in (page, overlay, region, orb):
        qtbot.addWidget(widget)
    settings = Settings()
    activator = Activator()

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
        activator,  # type: ignore[arg-type]
        logging.getLogger("test-ocr-mss-screen"),
        i18n,  # type: ignore[arg-type]
    )

    assert page.target_controls.isHidden()
    assert not page._screen_capture_note.isHidden()
    assert controller._target.selected_window() == desktop
    assert settings.current.ocr.region_width == 0
    assert settings.current.ocr.region_coordinate_space == "screen"

    controller._region_selected(CaptureRegion(100, 120, 500, 220))
    assert settings.current.ocr.window_title == "VRChat"
    assert settings.current.ocr.region_coordinate_space == "screen"
    assert activator.handles == []
    assert controller.shutdown()
