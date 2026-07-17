import logging

from PySide6.QtCore import Qt

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.prepare_chatbox_message import PrepareChatboxMessage
from vrctranslate.application.use_cases.send_chatbox_message import ChatboxSendQueue
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.translation import TranslationResult
from vrctranslate.presentation.qt.controllers.self_message_controller import SelfMessageController
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.windows.quick_input_window import QuickInputWindow


class _FakeI18n:
    def tr(self, key: str, **kwargs) -> str:
        return key

    @property
    def language_changed(self):
        return _FakeSignal()


class _FakeSignal:
    def connect(self, *args):
        pass


_FAKE_I18N = _FakeI18n()


class EchoFake:
    def translate(self, request, profile):
        return TranslationResult(
            request.request_id,
            request.text,
            "你好",
            request.source_language,
            request.target_language,
            request.purpose,
        )


class FailingFake:
    def translate(self, request, profile):
        raise RuntimeError("failure")


class GatewayFake:
    def __init__(self):
        self.messages = []
        self.typing = []

    def send_input(self, text, settings):
        self.messages.append(text)

    def send_typing(self, typing, settings):
        self.typing.append(typing)


class RepositoryFake:
    location = "memory://settings"

    def __init__(self):
        self.value = AppSettings()
        self.value.osc.min_interval_seconds = 0.1
        self.save_count = 0

    def load(self):
        return self.value

    def save(self, settings):
        self.value = settings
        self.save_count += 1


def _controller(qtbot, translator):
    page = SelfMessagePage(_FAKE_I18N)
    quick = QuickInputWindow(i18n=_FAKE_I18N)
    qtbot.addWidget(page)
    qtbot.addWidget(quick)
    gateway = GatewayFake()
    repository = RepositoryFake()
    controller = SelfMessageController(
        page,
        quick,
        TranslateText(translator),
        PrepareChatboxMessage(),
        ChatboxSendQueue(gateway),
        ManageSettings(repository),
        logging.getLogger("presentation-test"),
        _FAKE_I18N,
        None,
    )
    return page, quick, gateway, controller, repository


def test_enter_translates_and_sends_without_edit_or_buttons(qtbot) -> None:
    page, quick, gateway, controller, _ = _controller(qtbot, EchoFake())
    assert quick.input.placeholderText() == "quick_input.placeholder"
    quick.input.setText("hello")
    qtbot.keyPress(quick.input, Qt.Key.Key_Return)
    qtbot.waitUntil(lambda: gateway.messages == ["你好"], timeout=3000)
    assert quick.text == ""
    assert gateway.typing[:2] == [True, False]
    assert not hasattr(page, "translate_button")
    assert not hasattr(page, "translation_edit")
    controller.shutdown()


def test_translation_failure_restores_original(qtbot) -> None:
    _, quick, _, controller, _ = _controller(qtbot, FailingFake())
    quick.input.setText("restore me")
    qtbot.keyPress(quick.input, Qt.Key.Key_Return)
    qtbot.waitUntil(lambda: "restore me" in quick.text, timeout=3000)
    assert "fail" in quick.status.text().lower()
    controller.shutdown()


def test_quick_input_page_settings_are_saved_automatically(qtbot) -> None:
    page, _, _, controller, repository = _controller(qtbot, EchoFake())

    page.input_width_edit.setValue(640)
    page.input_topmost_check.setChecked(False)
    qtbot.waitUntil(lambda: repository.save_count > 0, timeout=1500)

    assert repository.value.ui.input_width == 640
    assert repository.value.ui.input_topmost is False
    assert not hasattr(page, "save_bar")
    controller.shutdown()


def test_bootstrap_composes_three_pages_and_non_topmost_main(qtbot, monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("VRC_TRANSLATE_HOME", str(tmp_path))
    from vrctranslate.bootstrap import build_main_window

    window = build_main_window()
    qtbot.addWidget(window)
    assert window.tabs.count() == 3
    assert window.windowTitle() == "VRCTranslate"
    assert not window.windowIcon().isNull()
    assert not bool(window.windowFlags() & Qt.WindowType.WindowStaysOnTopHint)
    assert "quick_input" in window._quick_window.input.placeholderText() or "Enter" in window._quick_window.input.placeholderText()
    assert not hasattr(window, "_capture_excluder")
    assert [window.navigation.item(i).sizeHint().height() for i in range(3)] == [
        44,
        44,
        44,
    ]
    assert all(not window.navigation.item(i).icon().isNull() for i in range(3))
    window.close()
