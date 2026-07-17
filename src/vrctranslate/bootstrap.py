from __future__ import annotations

from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate import __version__
from vrctranslate.application.use_cases.prepare_chatbox_message import (
    PrepareChatboxMessage,
)
from vrctranslate.application.use_cases.process_ocr_frame import ProcessOcrFrame
from vrctranslate.application.use_cases.send_chatbox_message import ChatboxSendQueue
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.infrastructure.capture.capture_router import CaptureRouter
from vrctranslate.infrastructure.capture.mss_capture import MssScreenCapture
from vrctranslate.infrastructure.capture.windows_graphics_capture import (
    WindowsGraphicsCapture,
)
from vrctranslate.infrastructure.capture.windows_api import WindowsApi
from vrctranslate.infrastructure.logging.setup import (
    clear_application_logs,
    configure_logging,
)
from vrctranslate.infrastructure.ocr.rapidocr_engine import RapidOcrEngine
from vrctranslate.infrastructure.osc.pythonosc_gateway import PythonOscGateway
from vrctranslate.infrastructure.paths import discover_app_paths
from vrctranslate.infrastructure.settings.json_repository import JsonSettingsRepository
from vrctranslate.infrastructure.translation.argos_model_manager import ArgosModelManager
from vrctranslate.infrastructure.translation.argos_translator import ArgosTranslator
from vrctranslate.infrastructure.translation.deepl_translator import DeepLTranslator
from vrctranslate.infrastructure.translation.echo_translator import EchoTranslator
from vrctranslate.infrastructure.translation.google_cloud_translator import GoogleCloudTranslator
from vrctranslate.infrastructure.translation.google_free_translator import GoogleFreeTranslator
from vrctranslate.infrastructure.translation.openai_compatible import OpenAICompatibleTranslator
from vrctranslate.infrastructure.translation.router import TranslationRouter
from vrctranslate.infrastructure.translation.tencent_translator import TencentTranslator
from vrctranslate.presentation.qt.application import run_qt_application
from vrctranslate.presentation.qt.controllers.ocr_controller import OcrController
from vrctranslate.presentation.qt.controllers.self_message_controller import (
    SelfMessageController,
)
from vrctranslate.presentation.qt.controllers.settings_controller import (
    SettingsController,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.main_window import MainWindow
from vrctranslate.presentation.qt.pages.ocr_page import OcrPage
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.windows.ocr_overlay_window import OcrOverlayWindow
from vrctranslate.presentation.qt.windows.quick_input_window import QuickInputWindow


def build_main_window() -> MainWindow:
    paths = discover_app_paths()
    paths.ensure_writable()
    logger = configure_logging(paths.data_root)
    settings = ManageSettings(JsonSettingsRepository(app_paths=paths))
    settings.load()

    model_manager = ArgosModelManager(paths)
    router = TranslationRouter(
        [
            EchoTranslator(),
            DeepLTranslator(),
            GoogleCloudTranslator(),
            GoogleFreeTranslator(),
            TencentTranslator(),
            ArgosTranslator(model_manager),
            OpenAICompatibleTranslator(),
        ]
    )
    translate_text = TranslateText(router)
    prepare_message = PrepareChatboxMessage()
    send_queue = ChatboxSendQueue(PythonOscGateway())
    windows_api = WindowsApi()
    capture = CaptureRouter(
        WindowsGraphicsCapture(windows_api),
        MssScreenCapture(windows_api),
    )
    capture.set_mode(settings.current.ocr.capture_backend)
    ocr_engine = RapidOcrEngine(settings.current.translation.ocr_route.source_language)
    process_ocr = ProcessOcrFrame(ocr_engine)

    i18n = I18nManager(settings.current.ui.language)

    self_page = SelfMessagePage(i18n)
    ocr_page = OcrPage(i18n)
    settings_page = SettingsPage(i18n)
    quick_window = QuickInputWindow(windows_api, i18n)
    ocr_overlay = OcrOverlayWindow(windows_api, i18n)
    window = MainWindow(
        self_page,
        ocr_page,
        settings_page,
        quick_window,
        ocr_overlay,
        settings,
        logger,
        i18n,
    )

    self_controller = SelfMessageController(
        self_page,
        quick_window,
        translate_text,
        prepare_message,
        send_queue,
        settings,
        logger,
        i18n,
        window,
    )
    ocr_controller = OcrController(
        ocr_page,
        ocr_overlay,
        capture,
        process_ocr,
        ocr_engine,
        translate_text,
        settings,
        logger,
        i18n,
        window,
    )
    settings_controller = SettingsController(
        settings_page,
        settings,
        translate_text,
        model_manager,
        lambda: clear_application_logs(logger),
        logger,
        window,
        i18n,
    )
    window.register_controllers(self_controller, ocr_controller, settings_controller)
    settings_controller.settings_changed.connect(self_controller.apply_settings)
    settings_controller.settings_changed.connect(ocr_controller.apply_settings)
    settings_controller.settings_changed.connect(window.apply_settings)
    settings_page.capture_test_requested.connect(ocr_controller.test_capture)
    ocr_controller.capture_preview_ready.connect(settings_page.set_capture_preview)
    settings_controller.status_bar_message.connect(window.show_status)
    window.apply_settings(settings.current)
    logger.info("application_started version=%s", __version__)
    return window


def main() -> int:
    WindowsApi().enable_dpi_awareness()
    return run_qt_application(build_main_window)
