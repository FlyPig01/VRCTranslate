from __future__ import annotations

import logging

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

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.presentation.qt.controllers.ocr_controller import OcrController
from vrctranslate.presentation.qt.controllers.self_message_controller import SelfMessageController
from vrctranslate.presentation.qt.controllers.settings_controller import SettingsController
from vrctranslate.presentation.qt.icon_resources import load_icon
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow
from vrctranslate.presentation.qt.windows.quick_input_window import QuickInputWindow


class MainWindow(QMainWindow):
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
        self._first_show = True
        self._closing = False
        self._changing_navigation = False
        self.setWindowTitle("VRCTranslate")
        self.setWindowIcon(load_icon("app.ico"))
        self.setMinimumSize(720, 520)
        self.resize(settings.current.ui.main_width, settings.current.ui.main_height)
        if settings.current.ui.main_x >= 0 and settings.current.ui.main_y >= 0:
            self.move(settings.current.ui.main_x, settings.current.ui.main_y)
        self._build_layout(self_page, ocr_page, settings_page)
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
    ) -> None:
        central = QWidget()
        central.setObjectName("mainSurface")
        root = QHBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)
        sidebar = QFrame()
        sidebar.setObjectName("sidebar")
        sidebar.setFixedWidth(176)
        side_layout = QVBoxLayout(sidebar)
        side_layout.setContentsMargins(16, 20, 16, 16)
        side_layout.setSpacing(0)
        brand = QHBoxLayout()
        brand.setSpacing(8)
        logo_icon = QLabel()
        logo_icon.setPixmap(load_icon("app.ico").pixmap(30, 30))
        logo = QLabel("VRCTranslate")
        logo.setObjectName("appName")
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
        self._nav_keys = ("nav.quick_input", "nav.ocr", "nav.settings")
        entries = (
            ("", "ui/nav_input.svg"),
            ("", "ui/nav_ocr.svg"),
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
        self._version_label.setText("v0.3 · PC Only")
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
        if previous == 2 and index != 2 and not self._settings_page.confirm_leave():
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
    ) -> None:
        self._self_controller = self_controller
        self._ocr_controller = ocr_controller
        self._settings_controller = settings_controller
        self_controller.status_bar_message.connect(self.statusBar().showMessage)
        ocr_controller.tray_state_changed.connect(self.set_tray_state)

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
        input_action = QAction("显示快捷输入", self)
        input_action.triggered.connect(self._quick_window.show_and_focus)
        show_action = QAction("显示主窗口", self)
        show_action.triggered.connect(self._show_main)
        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.close)
        menu = QMenu()
        menu.addAction(input_action)
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
        if not self._settings_page.confirm_leave():
            event.ignore()
            return
        self._closing = True
        if self._ocr_controller and not self._ocr_controller.shutdown():
            self._closing = False
            QMessageBox.warning(self, "正在停止", "OCR 仍在释放资源，请稍后再次退出。")
            event.ignore()
            return
        if self._self_controller:
            self._self_controller.shutdown()
        self._ocr_overlay.close_permanently()
        self._geometry_timer.stop()
        QThreadPool.globalInstance().waitForDone(10_000)
        if self.tray is not None:
            self.tray.hide()
        self._logger.info("application_stopped")
        event.accept()
