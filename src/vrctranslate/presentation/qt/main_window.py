from __future__ import annotations

import logging
from ctypes import wintypes

from PySide6.QtCore import QSize, QThreadPool, QTimer, Qt
from PySide6.QtGui import QAction, QCloseEvent, QShowEvent
from PySide6.QtWidgets import (
    QFrame,
    QHBoxLayout,
    QLabel,
    QListWidget,
    QListWidgetItem,
    QMainWindow,
    QMenu,
    QMessageBox,
    QStackedWidget,
    QStatusBar,
    QSystemTrayIcon,
    QVBoxLayout,
    QWidget,
)

from vrctranslate import __version__
from vrctranslate.application.dto import (
    DEFAULT_SELF_VOICE_HOTKEY,
    LEGACY_SELF_VOICE_HOTKEY,
    AppSettings,
)
from vrctranslate.application.ports.global_hotkeys import GlobalHotkeys
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.presentation.qt.controllers.ocr_controller import OcrController
from vrctranslate.presentation.qt.controllers.self_message_controller import SelfMessageController
from vrctranslate.presentation.qt.controllers.self_voice_controller import SelfVoiceController
from vrctranslate.presentation.qt.controllers.settings_controller import SettingsController
from vrctranslate.presentation.qt.controllers.voice_translation_controller import (
    VoiceTranslationController,
)
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.pages.voice_page import VoicePage
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow
from vrctranslate.presentation.qt.windows.quick_input_window import QuickInputWindow
from vrctranslate.presentation.qt.windows.voice_overlay_window import VoiceOverlayWindow


class MainWindow(QMainWindow):
    _WM_HOTKEY = 0x0312
    _HOTKEY_QUICK_INPUT = 0x5601
    _HOTKEY_SELF_VOICE = 0x5602

    def __init__(
        self,
        self_page: SelfMessagePage,
        ocr_page: OcrPage,
        settings_page: SettingsPage,
        quick_window: QuickInputWindow,
        ocr_overlay: OcrOverlayWindow,
        settings: ManageSettings,
        logger: logging.Logger,
        i18n: I18nManager,
        voice_page: VoicePage | None = None,
        voice_overlay: VoiceOverlayWindow | None = None,
        global_hotkeys: GlobalHotkeys | None = None,
    ) -> None:
        super().__init__()
        self._logger = logger
        self._settings = settings
        self._quick_window = quick_window
        self._ocr_overlay = ocr_overlay
        self._i18n = i18n
        self._self_controller: SelfMessageController | None = None
        self._ocr_controller: OcrController | None = None
        self._settings_controller: SettingsController | None = None
        self._voice_controller: VoiceTranslationController | None = None
        self._self_voice_controller: SelfVoiceController | None = None
        self._first_show = True
        self._closing = False
        self._changing_navigation = False
        self._self_page = self_page
        self._ocr_page = ocr_page
        self._voice_page = voice_page
        self._voice_overlay = voice_overlay
        self._global_hotkeys = global_hotkeys
        self._hotkeys_suspended = False
        self_page.hotkey_editing_changed.connect(
            self._set_global_hotkeys_suspended
        )
        self.setWindowTitle("VRCTranslate")
        self.setWindowIcon(load_icon("app.ico"))
        self.setMinimumSize(900, 560)
        self.resize(settings.current.ui.main_width, settings.current.ui.main_height)
        if settings.current.ui.main_x >= 0 and settings.current.ui.main_y >= 0:
            self.move(settings.current.ui.main_x, settings.current.ui.main_y)
        self._build_layout(self_page, ocr_page, settings_page, voice_page)
        self.setStatusBar(QStatusBar())
        self._retranslate_ui()
        self._geometry_timer = QTimer(self)
        self._geometry_timer.setSingleShot(True)
        self._geometry_timer.setInterval(500)
        self._geometry_timer.timeout.connect(self._save_main_geometry)
        self._setup_tray()
        i18n.language_changed.connect(lambda _: self._retranslate_ui())

    def _build_layout(
        self,
        self_page: SelfMessagePage,
        ocr_page: OcrPage,
        settings_page: SettingsPage,
        voice_page: VoicePage | None,
    ) -> None:
        central = QWidget()
        central.setObjectName("mainSurface")
        root = QHBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)
        sidebar = QFrame()
        sidebar.setObjectName("sidebar")
        sidebar.setFixedWidth(176)
        self._sidebar = sidebar
        side_layout = QVBoxLayout(sidebar)
        self._side_layout = side_layout
        side_layout.setContentsMargins(16, 20, 16, 16)
        side_layout.setSpacing(0)
        brand = QHBoxLayout()
        brand.setSpacing(8)
        logo_icon = QLabel()
        logo_icon.setPixmap(load_icon("app.ico").pixmap(30, 30))
        self._brand_icon = logo_icon
        logo = QLabel("VRCTranslate")
        logo.setObjectName("appName")
        self._brand_name = logo
        brand.addWidget(logo_icon)
        brand.addWidget(logo, 1)
        self._tagline = QLabel()
        self._tagline.setObjectName("appTagline")
        side_layout.addLayout(brand)
        side_layout.addWidget(self._tagline)
        side_layout.addSpacing(22)
        self.navigation = QListWidget()
        self.navigation.setObjectName("navigation")
        self.navigation.setSpacing(2)
        if voice_page is None:
            self._nav_keys = ("nav.quick_input", "nav.ocr", "nav.settings")
            entries = (
                ("", "ui/nav_input.svg"),
                ("", "ui/nav_ocr.svg"),
                ("", "ui/nav_settings.svg"),
            )
        else:
            self._nav_keys = (
                "nav.quick_input",
                "nav.ocr",
                "nav.voice",
                "nav.settings",
            )
            entries = (
                ("", "ui/nav_input.svg"),
                ("", "ui/nav_ocr.svg"),
                ("", "ui/nav_voice.svg"),
                ("", "ui/nav_settings.svg"),
            )
        self.navigation.setIconSize(QSize(20, 20))
        for (_, icon) in entries:
            item = QListWidgetItem(load_icon(icon), "")
            item.setTextAlignment(Qt.AlignmentFlag.AlignVCenter)
            item.setSizeHint(QSize(0, 44))
            self.navigation.addItem(item)
        side_layout.addWidget(self.navigation, 1)
        self._version_label = QLabel()
        self._version_label.setObjectName("appTagline")
        self._version_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        side_layout.addWidget(self._version_label)

        self.pages = QStackedWidget()
        self.pages.addWidget(self_page)
        self.pages.addWidget(ocr_page)
        if voice_page is not None:
            self.pages.addWidget(voice_page)
        self.pages.addWidget(settings_page)
        self._settings_page = settings_page
        self.tabs = self.pages  # Stable public handle used by lightweight UI tests.
        self.navigation.currentRowChanged.connect(self._navigate)
        self.navigation.setCurrentRow(0)
        root.addWidget(sidebar)
        root.addWidget(self.pages, 1)
        self.setCentralWidget(central)

    def _retranslate_ui(self) -> None:
        t = self._i18n.tr
        self._tagline.setText(t("app.tagline"))
        self._version_label.setText(f"v{__version__} · PC Only")
        for i, key in enumerate(self._nav_keys):
            item = self.navigation.item(i)
            if item:
                item.setText(t(key))
        self.statusBar().showMessage(t("status.ready"))
        tray = getattr(self, "tray", None)
        if tray is not None:
            tray.setToolTip("VRCTranslate")

    def _navigate(self, index: int) -> None:
        if self._changing_navigation:
            return
        previous = self.pages.currentIndex()
        previous_page = self.pages.widget(previous)
        if index != previous and not self._confirm_page_leave(previous_page):
            self._changing_navigation = True
            self.navigation.setCurrentRow(previous)
            self._changing_navigation = False
            return
        self.pages.setCurrentIndex(index)

    def register_controllers(
        self,
        self_controller: SelfMessageController,
        ocr_controller: OcrController,
        settings_controller: SettingsController,
        voice_controller: VoiceTranslationController | None = None,
        self_voice_controller: SelfVoiceController | None = None,
    ) -> None:
        self._self_controller = self_controller
        self._ocr_controller = ocr_controller
        self._settings_controller = settings_controller
        self._voice_controller = voice_controller
        self._self_voice_controller = self_voice_controller
        self_controller.status_bar_message.connect(self.statusBar().showMessage)
        ocr_controller.tray_state_changed.connect(self.set_tray_state)
        if voice_controller is not None:
            voice_controller.status_bar_message.connect(self.show_status)
        if self_voice_controller is not None:
            self_voice_controller.status_bar_message.connect(self.show_status)
            self_voice_controller.hotkeys_changed.connect(
                self._configure_global_hotkeys
            )
        self_controller.hotkeys_changed.connect(self._configure_global_hotkeys)
        self._configure_global_hotkeys()

    def apply_settings(self, settings: object) -> None:
        if not isinstance(settings, AppSettings):
            return
        # The main window deliberately never receives WindowStaysOnTopHint.
        if not self.isVisible():
            self.resize(settings.ui.main_width, settings.ui.main_height)

    def show_status(self, message: str, timeout: int) -> None:
        self.statusBar().showMessage(message, timeout)

    def _setup_tray(self) -> None:
        if not QSystemTrayIcon.isSystemTrayAvailable():
            self.tray = None
            return
        self._tray_icons = {
            "normal": load_icon("tray.png"),
            "ocr": load_icon("tray_ocr.png"),
            "error": load_icon("tray_error.png"),
        }
        self.tray = QSystemTrayIcon(self._tray_icons["normal"], self)
        self._tray_input_action = QAction("显示快捷输入", self)
        self._tray_input_action.triggered.connect(self._quick_window.show_and_focus)
        show_action = QAction("显示主窗口", self)
        show_action.triggered.connect(self._show_main)
        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.close)
        menu = QMenu()
        menu.addAction(self._tray_input_action)
        menu.addAction(show_action)
        menu.addSeparator()
        menu.addAction(quit_action)
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(
            lambda reason: self._quick_window.show_and_focus()
            if reason == QSystemTrayIcon.ActivationReason.DoubleClick
            else None
        )
        self.tray.show()

    def _show_main(self) -> None:
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def set_tray_state(self, state: str) -> None:
        if self.tray is None:
            return
        self.tray.setIcon(self._tray_icons.get(state, self._tray_icons["normal"]))

    def _configure_global_hotkeys(self) -> None:
        manager = self._global_hotkeys
        if manager is None or self._hotkeys_suspended:
            return
        settings = self._settings.current
        hwnd = int(self.winId())
        failures: list[str] = []
        self._self_page.set_hotkey_registration_status("quick_input")
        self._self_page.set_hotkey_registration_status("self_voice")
        quick_shortcut = settings.ui.quick_input_hotkey
        quick_registered = manager.register(
            hwnd,
            self._HOTKEY_QUICK_INPUT,
            quick_shortcut,
        )
        if not quick_registered:
            failures.append(quick_shortcut)
            self._self_page.set_hotkey_registration_status(
                "quick_input",
                "error",
                self._i18n.tr(
                    "hotkey.conflict_inline",
                    shortcut=quick_shortcut,
                ),
            )

        voice_shortcut = settings.self_voice.toggle_hotkey
        voice_registered = manager.register(
            hwnd,
            self._HOTKEY_SELF_VOICE,
            voice_shortcut,
        )
        if (
            not voice_registered
            and voice_shortcut.casefold()
            == LEGACY_SELF_VOICE_HOTKEY.casefold()
        ):
            voice_registered = manager.register(
                hwnd,
                self._HOTKEY_SELF_VOICE,
                DEFAULT_SELF_VOICE_HOTKEY,
            )
            if voice_registered:
                settings.self_voice.toggle_hotkey = DEFAULT_SELF_VOICE_HOTKEY
                self._self_page.load_self_voice_settings(settings.self_voice)
                try:
                    self._settings.save(settings)
                except OSError as exc:
                    self._logger.warning(
                        "global_hotkey_fallback_save_failed error=%s",
                        type(exc).__name__,
                    )
                self._logger.info(
                    "global_hotkey_fallback_applied shortcut=%s",
                    DEFAULT_SELF_VOICE_HOTKEY,
                )
        if not voice_registered:
            failures.append(voice_shortcut)
        if not voice_registered:
            self._self_page.set_hotkey_registration_status(
                "self_voice",
                "error",
                self._i18n.tr(
                    "hotkey.conflict_inline",
                    shortcut=voice_shortcut,
                ),
            )
        if failures:
            self._logger.warning(
                "global_hotkey_registration_failed shortcuts=%s",
                ",".join(failures),
            )
            self.show_status(
                self._i18n.tr(
                    "hotkey.register_failed",
                    shortcut=", ".join(failures),
                ),
                7000,
            )
        tray = getattr(self, "_tray_input_action", None)
        if tray is not None:
            suffix = (
                f"\t{settings.ui.quick_input_hotkey}"
                if settings.ui.quick_input_hotkey
                else ""
            )
            tray.setText(f"显示快捷输入{suffix}")

    def _set_global_hotkeys_suspended(self, suspended: bool) -> None:
        self._hotkeys_suspended = suspended
        manager = self._global_hotkeys
        if manager is None:
            return
        if suspended:
            manager.shutdown()
        else:
            self._configure_global_hotkeys()

    def nativeEvent(self, event_type, message):  # type: ignore[no-untyped-def]
        try:
            native_message = wintypes.MSG.from_address(int(message))
        except (TypeError, ValueError):
            return super().nativeEvent(event_type, message)
        if native_message.message == self._WM_HOTKEY:
            if self._handle_global_hotkey(int(native_message.wParam)):
                return True, 0
        return super().nativeEvent(event_type, message)

    def _handle_global_hotkey(self, hotkey_id: int) -> bool:
        """Dispatch one registered shortcut without coupling tests to native MSG."""

        if hotkey_id == self._HOTKEY_QUICK_INPUT:
            self._logger.info("global_hotkey_triggered action=quick_input")
            self._quick_window.show_and_focus()
            return True
        if (
            hotkey_id == self._HOTKEY_SELF_VOICE
            and self._self_voice_controller is not None
        ):
            self._logger.info("global_hotkey_triggered action=self_voice")
            self._self_voice_controller.toggle_enabled()
            return True
        return False

    def showEvent(self, event: QShowEvent) -> None:
        super().showEvent(event)
        if self._first_show:
            self._first_show = False
            ui = self._settings.current.ui
            if ui.main_x < 0 or ui.main_y < 0:
                screen = self.screen()
                if screen is not None:
                    area = screen.availableGeometry()
                    self.move(
                        area.center().x() - self.width() // 2,
                        area.center().y() - self.height() // 2,
                    )
            QTimer.singleShot(100, self._quick_window.show_and_focus)

    def resizeEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().resizeEvent(event)
        if hasattr(self, "_geometry_timer"):
            self._geometry_timer.start()

    def _update_sidebar_mode(self) -> None:
        """Compatibility hook: the sidebar is intentionally always complete."""

        self._sidebar.setFixedWidth(176)
        self._side_layout.setContentsMargins(16, 20, 16, 16)
        self._brand_name.setVisible(True)
        self._tagline.setVisible(True)
        self._version_label.setVisible(True)
        self._sidebar.setProperty("compact", False)
        for index, key in enumerate(self._nav_keys):
            item = self.navigation.item(index)
            if item is not None:
                item.setText(self._i18n.tr(key))
                item.setTextAlignment(Qt.AlignmentFlag.AlignVCenter)

    def _confirm_page_leave(self, page: object) -> bool:
        if page is self._settings_page:
            return self._settings_page.confirm_leave()
        if not bool(getattr(page, "has_unsaved_changes", False)):
            return True
        t = self._i18n.tr
        box = QMessageBox(self)
        box.setWindowTitle(t("page.settings.leave_title"))
        box.setText(t("page.settings.leave_text"))
        box.setInformativeText(t("page.settings.leave_info"))
        save = box.addButton(t("page.settings.leave_save"), QMessageBox.ButtonRole.AcceptRole)
        discard = box.addButton(t("page.settings.leave_discard"), QMessageBox.ButtonRole.DestructiveRole)
        box.addButton(t("page.settings.leave_cancel"), QMessageBox.ButtonRole.RejectRole)
        box.exec()
        clicked = box.clickedButton()
        if clicked is save:
            getattr(page, "save_requested").emit()
            return not bool(getattr(page, "has_unsaved_changes", False))
        if clicked is discard:
            getattr(page, "discard_requested").emit()
            return True
        return False

    def moveEvent(self, event) -> None:  # type: ignore[no-untyped-def]
        super().moveEvent(event)
        if hasattr(self, "_geometry_timer"):
            self._geometry_timer.start()

    def _save_main_geometry(self) -> None:
        if self._closing:
            return
        ui = self._settings.current.ui
        ui.main_width = self.width()
        ui.main_height = self.height()
        ui.main_x = self.x()
        ui.main_y = self.y()
        self._settings.save(self._settings.current)

    def closeEvent(self, event: QCloseEvent) -> None:
        pages = [self._self_page, self._ocr_page]
        if self._voice_page is not None:
            pages.append(self._voice_page)
        pages.append(self._settings_page)
        for page in pages:
            if not self._confirm_page_leave(page):
                event.ignore()
                return
        self._closing = True
        if self._ocr_controller and not self._ocr_controller.shutdown():
            self._closing = False
            QMessageBox.warning(self, "正在停止", "OCR 仍在释放资源，请稍后再次退出。")
            event.ignore()
            return
        if self._self_voice_controller:
            self._self_voice_controller.shutdown()
        if self._self_controller:
            self._self_controller.shutdown()
        if self._voice_controller:
            self._voice_controller.shutdown()
        elif self._voice_overlay is not None:
            self._voice_overlay.close_permanently()
        self._ocr_overlay.close_permanently()
        self._geometry_timer.stop()
        QThreadPool.globalInstance().waitForDone(10_000)
        if self.tray is not None:
            self.tray.hide()
        if self._global_hotkeys is not None:
            self._global_hotkeys.shutdown()
        self._logger.info("application_stopped")
        event.accept()
