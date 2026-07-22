from __future__ import annotations

import logging

from PySide6.QtGui import QKeySequence

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.domain.speech import (
    AudioFrame,
    MicrophoneDevice,
    SpeechProfileValidationResult,
    SpeechRecognitionResult,
)
from vrctranslate.presentation.qt.controllers.self_voice_controller import (
    SelfVoiceController,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage


class _Repository:
    location = "memory://settings"

    def __init__(self, settings: AppSettings | None = None) -> None:
        self.value = settings or AppSettings()
        self.save_count = 0

    def load(self) -> AppSettings:
        return self.value

    def save(self, settings: AppSettings) -> None:
        self.value = settings
        self.save_count += 1


class _MicrophoneCapture:
    def __init__(self) -> None:
        self.running = False
        self.start_count = 0
        self.stop_count = 0
        self.selected_device = ""
        self.on_frame = None
        self.on_error = None

    def list_devices(self):
        return [
            MicrophoneDevice("7", "Test microphone", True, "Windows WASAPI"),
            MicrophoneDevice("8", "Alternate microphone", False, "Windows WASAPI"),
        ]

    def resolve_device_id(self, device_id):
        return str(device_id) if str(device_id) in {"7", "8"} else ""

    def start(self, device_id, on_frame, *, on_error=None):
        self.running = True
        self.start_count += 1
        self.selected_device = device_id
        self.on_frame = on_frame
        self.on_error = on_error

    def stop(self):
        self.running = False
        self.stop_count += 1


class _SpeechRecognizer:
    def __init__(self) -> None:
        self.transcribe_calls = 0
        self.released = False

    def validate_profile(self, profile):
        assert profile.provider == "local_offline"
        return SpeechProfileValidationResult("verified", "ok")

    def transcribe(self, request, profile):
        assert profile.model == "sensevoice-small-int8"
        self.transcribe_calls += 1
        return SpeechRecognitionResult(request.request_id, "こんにちは", "ja")

    def release(self, profile):
        assert profile.provider == "local_offline"
        self.released = True


class _Windows:
    def __init__(self, foreground: bool = False) -> None:
        self.foreground = foreground

    def list_windows(self):
        return [
            WindowInfo(11, "VRChat", 0, 0, 1280, 720, "VRChat.exe", 101)
        ]

    def is_foreground_window(self, hwnd):
        return self.foreground and hwnd == 11


class _SelfMessages:
    def __init__(self, accepted: bool = True) -> None:
        self.accepted = accepted
        self.messages: list[tuple[str, str]] = []

    def submit_voice_text(self, text: str, detected_language: str = "") -> bool:
        self.messages.append((text, detected_language))
        return self.accepted


def _controller(qtbot, settings: AppSettings, *, foreground: bool = False):
    page = SelfMessagePage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    capture = _MicrophoneCapture()
    speech = _SpeechRecognizer()
    windows = _Windows(foreground)
    messages = _SelfMessages()
    repository = _Repository(settings)
    controller = SelfVoiceController(
        page,
        capture,  # type: ignore[arg-type]
        speech,  # type: ignore[arg-type]
        ManageSettings(repository),
        windows,  # type: ignore[arg-type]
        messages,  # type: ignore[arg-type]
        logging.getLogger("test-self-voice"),
        I18nManager("zh_CN"),
    )
    return controller, page, capture, speech, windows, messages, repository


def _pcm(value: int, milliseconds: int = 100) -> bytes:
    return int(value).to_bytes(2, "little", signed=True) * (16 * milliseconds)


def test_self_voice_is_disabled_by_default_and_does_not_open_microphone(qtbot) -> None:
    controller, _, capture, speech, _, _, _ = _controller(qtbot, AppSettings())

    qtbot.wait(50)

    assert capture.start_count == 0
    assert capture.running is False
    controller.shutdown()
    assert speech.released


def test_self_voice_foreground_scope_waits_until_vrchat_is_foreground(qtbot) -> None:
    settings = AppSettings()
    settings.self_voice.enabled = True
    settings.self_voice.activation_scope = "vrchat_foreground"
    controller, _, capture, _, windows, _, _ = _controller(qtbot, settings)

    qtbot.wait(50)
    assert capture.running is False

    windows.foreground = True
    controller._evaluate_capture()
    qtbot.waitUntil(lambda: capture.running, timeout=3000)

    controller.shutdown()
    assert capture.running is False


def test_self_voice_submits_one_complete_sentence_to_self_message_queue(qtbot) -> None:
    settings = AppSettings()
    settings.self_voice.enabled = True
    settings.self_voice.activation_scope = "always"
    settings.self_voice.microphone_id = "7"
    controller, page, capture, speech, _, messages, _ = _controller(qtbot, settings)
    qtbot.waitUntil(lambda: capture.running, timeout=3000)

    assert capture.selected_device == "7"
    assert capture.on_frame is not None
    voiced = AudioFrame(_pcm(2000))
    silent = AudioFrame(_pcm(0))
    for _ in range(5):
        capture.on_frame(silent)
    for _ in range(4):
        capture.on_frame(voiced)
    qtbot.wait(50)
    assert speech.transcribe_calls == 0
    for _ in range(7):
        capture.on_frame(silent)

    qtbot.waitUntil(
        lambda: messages.messages == [("こんにちは", "ja")],
        timeout=3000,
    )
    assert speech.transcribe_calls == 1
    assert "こんにちは" in page._self_voice_original_label.text()

    controller.shutdown()
    assert capture.running is False
    assert speech.released


def test_self_voice_settings_are_saved_automatically(qtbot) -> None:
    controller, page, _, _, _, _, repository = _controller(qtbot, AppSettings())

    page.microphone_combo.setCurrentIndex(page.microphone_combo.findData("8"))
    page.self_voice_language_combo.setCurrentIndex(
        page.self_voice_language_combo.findData("ja")
    )
    page.self_voice_scope_combo.setCurrentIndex(
        page.self_voice_scope_combo.findData("vrchat_running")
    )
    page.self_voice_hotkey_control.begin_edit()
    page.self_voice_hotkey_edit.setKeySequence(QKeySequence("Ctrl+Shift+M"))
    page.self_voice_hotkey_control.confirm_edit()
    qtbot.waitUntil(lambda: repository.save_count > 0, timeout=1500)

    assert repository.value.self_voice.microphone_id == "8"
    assert repository.value.self_voice.source_language == "ja"
    assert repository.value.self_voice.activation_scope == "vrchat_running"
    assert repository.value.self_voice.toggle_hotkey == "Ctrl+Shift+M"
    controller.shutdown()


def test_self_voice_hotkey_requires_confirmation_and_can_restore_default(qtbot) -> None:
    controller, page, _, _, _, _, repository = _controller(qtbot, AppSettings())
    changes: list[bool] = []
    page.hotkey_editing_changed.connect(changes.append)

    page.self_voice_hotkey_control.begin_edit()
    page.self_voice_hotkey_edit.setKeySequence(QKeySequence("Ctrl+Shift+M"))
    page.self_voice_hotkey_control.cancel_edit()
    assert repository.value.self_voice.toggle_hotkey == "Ctrl+F8"

    page.self_voice_hotkey_control.begin_edit()
    page.self_voice_hotkey_edit.setKeySequence(QKeySequence("Ctrl+Shift+M"))
    page.self_voice_hotkey_control.restore_default()
    page.self_voice_hotkey_control.confirm_edit()

    assert repository.value.self_voice.toggle_hotkey == "Ctrl+F8"
    assert changes == [True, False, True, False]
    controller.shutdown()


def test_self_voice_status_panel_animates_and_icon_button_is_accessible(qtbot) -> None:
    page = SelfMessagePage(I18nManager("zh_CN"))
    qtbot.addWidget(page)

    page.set_self_voice_status("Recognizing", "recognizing")
    assert page._voice_activity._timer.isActive()
    assert page._voice_status_panel.property("state") == "recognizing"

    page.self_voice_toggle_button.blockSignals(True)
    page.self_voice_toggle_button.setChecked(True)
    page.self_voice_toggle_button.blockSignals(False)
    page._sync_voice_toggle_button()
    assert not page.self_voice_toggle_button.icon().isNull()
    assert page.self_voice_toggle_button.toolTip()
    assert page.self_voice_toggle_button.accessibleName()

    page.set_self_voice_status("Done", "success")
    assert not page._voice_activity._timer.isActive()

    page.set_microphone_level(1800)
    assert page.microphone_level.height() == 14
    assert page._level_animation.state().name == "Running"
    qtbot.waitUntil(lambda: page.microphone_level.value() > 0, timeout=500)


def test_microphone_list_hides_default_duplicate_and_test_confirms_audio(qtbot) -> None:
    controller, page, capture, _, _, _, _ = _controller(qtbot, AppSettings())

    assert page.microphone_combo.count() == 2
    assert page.microphone_combo.itemData(0) == ""
    assert "Test microphone" in page.microphone_combo.itemText(0)
    assert page.microphone_combo.itemData(1) == "8"

    page.microphone_test_button.click()
    qtbot.waitUntil(lambda: capture.running, timeout=1000)
    assert capture.on_frame is not None
    assert "你好，这是麦克风测试。" in page._self_voice_status.text()
    capture.on_frame(AudioFrame(_pcm(1200)))
    page.microphone_test_button.click()

    assert capture.running is False
    assert "当前设备可以使用" in page._self_voice_status.text()
    controller.shutdown()


def test_self_voice_reports_when_translation_queue_rejects_sentence(qtbot) -> None:
    settings = AppSettings()
    settings.self_voice.enabled = True
    settings.self_voice.activation_scope = "always"
    controller, page, _, _, _, messages, _ = _controller(qtbot, settings)
    messages.accepted = False

    controller._segment_recognized(
        SpeechRecognitionResult("test", "hello", "en"),
        controller._generation,
    )

    assert "处理速度跟不上" in page._self_voice_status.text()
    controller.shutdown()
