from __future__ import annotations

import logging

from PySide6.QtCore import QAbstractAnimation, Qt
from PySide6.QtGui import QGuiApplication
from PySide6.QtWidgets import QLabel, QPushButton, QScrollArea, QTreeWidget

from vrctranslate.application.dto import AppSettings, SpeechRecognitionProfile
from vrctranslate.application.speech_profiles import (
    profile_validation_state,
    set_profile_validation,
    speech_service_descriptor,
)
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.domain.speech import (
    AudioFrame,
    SpeechProfileValidationResult,
    SpeechServiceCapabilities,
    SpeechStreamEvent,
    VoiceCaption,
)
from vrctranslate.presentation.qt.controllers.voice_translation_controller import (
    VoiceTranslationController,
)
from vrctranslate.presentation.qt.controllers.settings_controller import SettingsController
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings.add_speech_profile_dialog import (
    AddSpeechProfileDialog,
)
from vrctranslate.presentation.qt.pages.settings.voice_settings_page import (
    VoiceSettingsPage,
)
from vrctranslate.presentation.qt.pages.voice_page import VoicePage
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.windows.voice_overlay_window import VoiceOverlayWindow


class _Repository:
    location = "memory://settings"

    def __init__(self) -> None:
        self.value = AppSettings()
        self.save_count = 0

    def load(self):
        return self.value

    def save(self, settings):
        self.value = settings
        self.save_count += 1


class _Windows:
    def list_windows(self):
        return [
            WindowInfo(11, "VRChat", 0, 0, 1280, 720, "VRChat.exe", 101),
            WindowInfo(12, "Test video", 0, 0, 900, 600, "vlc.exe", 202),
            WindowInfo(13, "Browser audio", 0, 0, 900, 600, "chrome.exe", 303),
        ]


class _Capture:
    def __init__(self) -> None:
        self.running = False
        self.started_process = None
        self.include_tree = None
        self.on_frame = None

    def start(self, process_id, on_frame, *, include_process_tree=True, on_error=None):
        del on_error
        self.started_process = process_id
        self.include_tree = include_process_tree
        self.on_frame = on_frame
        self.running = True

    def stop(self):
        self.running = False


class _StreamSession:
    def __init__(self) -> None:
        self.frames = []
        self.cancelled = False

    def push_audio(self, frame):
        self.frames.append(frame)

    def close(self):
        self.cancelled = True

    def cancel(self):
        self.cancelled = True


class _Speech:
    def __init__(self) -> None:
        self.session = _StreamSession()
        self.on_event = None

    def capabilities(self, profile):
        return speech_service_descriptor(profile.provider).capabilities

    def open_session(self, profile, config, on_event, on_error):
        del profile, config, on_error
        self.on_event = on_event
        return self.session

    def validate_profile(self, profile):
        del profile
        return SpeechProfileValidationResult("verified", "ok")


class _FixedLanguageSpeech(_Speech):
    def capabilities(self, profile):
        return SpeechServiceCapabilities(
            profile.provider,
            streaming_audio=True,
            partial_transcript=True,
            final_transcript=True,
            source_language_auto=False,
        )


class _TranslateVoice:
    def execute(self, original, detected_language, sequence, settings):
        del settings
        return VoiceCaption(sequence, original, "你好", detected_language)


class _FailTranslateVoice:
    def execute(self, original, detected_language, sequence, settings):
        del original, detected_language, sequence, settings
        raise ValueError("synthetic translation failure")


class _RecordingTranslateVoice(_TranslateVoice):
    def __init__(self) -> None:
        self.calls: list[tuple[str, str]] = []

    def execute(self, original, detected_language, sequence, settings):
        self.calls.append((original, detected_language))
        return super().execute(original, detected_language, sequence, settings)


def _verified_repository() -> _Repository:
    repository = _Repository()
    profile = SpeechRecognitionProfile(
        id="speech-test",
        name="Test realtime ASR",
        provider="tencent_realtime",
        api_key="secret-key",
        model="16k_zh",
        options={"app_id": "123", "secret_id": "secret-id"},
    )
    set_profile_validation(profile, "verified", "ok")
    repository.value.voice.asr_profiles = [profile]
    repository.value.voice.asr_profile_id = profile.id
    return repository


def test_voice_page_accepts_non_vrchat_processes(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    windows = _Windows().list_windows()

    page.set_target_windows(windows, 202)

    assert page.selected_process_id == 202
    assert page.selected_window().process_name == "vlc.exe"
    assert "Test video" in page.target_combo.currentText()


def test_voice_capture_streams_frames_and_translates_final_result(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(page)
    qtbot.addWidget(overlay)
    repository = _verified_repository()
    capture = _Capture()
    speech = _Speech()
    controller = VoiceTranslationController(
        page,
        overlay,
        capture,
        speech,  # type: ignore[arg-type]
        _TranslateVoice(),  # type: ignore[arg-type]
        ManageSettings(repository),
        _Windows(),  # type: ignore[arg-type]
        logging.getLogger("test-voice-controller"),
        I18nManager("zh_CN"),
    )

    page.target_combo.setCurrentIndex(page.target_combo.findData(202))
    overlay.show_overlay()
    qtbot.waitUntil(lambda: not overlay._animating_geometry, timeout=1000)
    assert overlay.recognition_button.text() == ""
    assert overlay.recognition_button.toolTip() == "开始识别"
    start_icon_key = overlay.recognition_button.icon().cacheKey()
    assert start_icon_key
    qtbot.mouseClick(overlay.recognition_button, Qt.MouseButton.LeftButton)
    qtbot.waitUntil(lambda: capture.running, timeout=3000)
    qtbot.waitUntil(
        lambda: not overlay.is_collapsed()
        and not overlay._animating_geometry,
        timeout=1000,
    )

    assert overlay.isVisible()
    assert not overlay.is_collapsed()
    assert overlay.recognition_button.text() == ""
    assert overlay.recognition_button.toolTip() == "停止识别"
    assert overlay.recognition_button.icon().cacheKey() != start_icon_key
    assert capture.started_process == 202
    assert capture.include_tree is True
    assert repository.value.voice.target_process_name == "vlc.exe"
    assert capture.on_frame is not None
    for _ in range(4):
        capture.on_frame(AudioFrame(b"\xd0\x07" * 1600))
    assert speech.session.frames

    speech.on_event(SpeechStreamEvent("partial_transcript", "hello"))
    qtbot.waitUntil(lambda: page._last_original == "hello", timeout=1000)
    speech.on_event(SpeechStreamEvent("final_transcript", "hello", "sentence-1", "en"))
    qtbot.waitUntil(lambda: bool(overlay._captions), timeout=3000)

    assert overlay._captions[-1].translated == "你好"
    translations = overlay.findChildren(QLabel, "voiceCaptionTranslation")
    assert any(label.text() == "你好" for label in translations)
    qtbot.mouseClick(overlay.recognition_button, Qt.MouseButton.LeftButton)
    qtbot.waitUntil(lambda: not capture.running, timeout=1000)
    qtbot.waitUntil(overlay.is_collapsed, timeout=1000)
    assert overlay.is_collapsed()
    assert overlay.recognition_button.text() == ""
    assert overlay.recognition_button.toolTip() == "开始识别"
    assert overlay.recognition_button.icon().cacheKey() == start_icon_key
    assert speech.session.cancelled
    controller.shutdown()


def test_voice_start_failure_is_visible_inside_overlay(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(page)
    qtbot.addWidget(overlay)
    repository = _Repository()
    capture = _Capture()
    controller = VoiceTranslationController(
        page,
        overlay,
        capture,
        _Speech(),  # type: ignore[arg-type]
        _TranslateVoice(),  # type: ignore[arg-type]
        ManageSettings(repository),
        _Windows(),  # type: ignore[arg-type]
        logging.getLogger("test-voice-start-feedback"),
        I18nManager("zh_CN"),
    )
    page.target_combo.setCurrentIndex(page.target_combo.findData(202))
    overlay.show_overlay()
    qtbot.waitUntil(lambda: not overlay._animating_geometry, timeout=1000)

    qtbot.mouseClick(overlay.recognition_button, Qt.MouseButton.LeftButton)

    qtbot.waitUntil(lambda: overlay.error_label.isVisible(), timeout=1000)
    assert "尚未配置实时语音识别档案" in overlay.error_label.text()
    assert capture.running is False
    controller.shutdown()


def test_voice_overlay_start_button_works_with_fixed_language_asr_and_auto_text_route(
    qtbot,
) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(page)
    qtbot.addWidget(overlay)
    repository = _verified_repository()
    assert repository.value.translation.voice_route.source_language == "auto"
    capture = _Capture()
    controller = VoiceTranslationController(
        page,
        overlay,
        capture,
        _FixedLanguageSpeech(),  # type: ignore[arg-type]
        _TranslateVoice(),  # type: ignore[arg-type]
        ManageSettings(repository),
        _Windows(),  # type: ignore[arg-type]
        logging.getLogger("test-fixed-language-start"),
        I18nManager("zh_CN"),
    )
    page.target_combo.setCurrentIndex(page.target_combo.findData(202))
    overlay.show_overlay()
    qtbot.waitUntil(lambda: not overlay._animating_geometry, timeout=1000)

    qtbot.mouseClick(overlay.recognition_button, Qt.MouseButton.LeftButton)

    qtbot.waitUntil(lambda: capture.running, timeout=3000)
    assert overlay._recognition_state == "running"
    controller.shutdown()


def test_voice_overlay_settings_live_on_voice_page_and_auto_save(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(page)
    qtbot.addWidget(overlay)
    repository = _verified_repository()
    controller = VoiceTranslationController(
        page,
        overlay,
        _Capture(),
        _Speech(),  # type: ignore[arg-type]
        _TranslateVoice(),  # type: ignore[arg-type]
        ManageSettings(repository),
        _Windows(),  # type: ignore[arg-type]
        logging.getLogger("test-voice-overlay-settings"),
        I18nManager("zh_CN"),
    )

    page.overlay_opacity_edit.setValue(72)
    page.display_mode_combo.setCurrentIndex(
        page.display_mode_combo.findData("translation")
    )
    qtbot.waitUntil(lambda: repository.save_count > 0, timeout=1500)

    assert repository.value.voice.overlay.opacity == 0.72
    assert repository.value.voice.overlay.show_original is False
    assert repository.value.voice.overlay.display_mode == "translation"
    assert "不会保存到磁盘" in page._subtitle.text()
    controller.shutdown()


def test_voice_starting_state_has_distinct_disabled_icon(qtbot) -> None:
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(overlay)
    idle_icon = overlay.recognition_button.icon().cacheKey()

    overlay.set_recognition_state("starting")

    assert overlay.recognition_button.icon().cacheKey() != idle_icon
    assert overlay.recognition_button.toolTip() == "正在启动…"
    assert not overlay.recognition_button.isEnabled()


def test_voice_overlay_keeps_only_configured_caption_count(qtbot) -> None:
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(overlay)
    settings = AppSettings().voice.overlay
    settings.max_items = 2
    overlay.apply_settings(settings)

    overlay.add_caption(VoiceCaption(1, "one", "一"))
    overlay.add_caption(VoiceCaption(2, "two", "二"))
    overlay.add_caption(VoiceCaption(3, "three", "三"))

    assert [caption.sequence for caption in overlay._captions] == [2, 3]


def test_voice_page_previews_overlay_style_changes(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    page.load_settings(AppSettings())

    page.overlay_opacity_edit.setValue(63)
    page.overlay_font_edit.setValue(30)
    page.display_mode_combo.setCurrentIndex(
        page.display_mode_combo.findData("translation")
    )

    assert page.overlay_style_preview.objectName() == "voiceOverlaySurface"
    assert page._preview_opacity_effect.opacity() == 0.63
    rendered_pixels = (
        page._preview_translation.font().pointSizeF()
        * page._preview_translation.logicalDpiY()
        / 72
    )
    assert round(rendered_pixels) == 30
    assert page._preview_original.isHidden()
    assert not page._preview_translation.isHidden()

    page.display_mode_combo.setCurrentIndex(
        page.display_mode_combo.findData("original")
    )

    assert not page._preview_original.isHidden()
    assert page._preview_translation.isHidden()
    original_pixels = (
        page._preview_original.font().pointSizeF()
        * page._preview_original.logicalDpiY()
        / 72
    )
    assert round(original_pixels) == 30


def test_voice_overlay_switches_between_caption_and_animated_orb(qtbot) -> None:
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(overlay)
    settings = AppSettings().voice.overlay
    settings.width = 640
    settings.height = 240
    overlay.apply_settings(settings)
    geometries: list[tuple[int, int, int, int]] = []
    overlay.geometry_changed.connect(
        lambda x, y, width, height: geometries.append((x, y, width, height))
    )

    screen = QGuiApplication.primaryScreen()
    assert screen is not None
    area = screen.availableGeometry()
    overlay.set_recognition_running(False)
    qtbot.waitExposed(overlay)
    overlay.move(area.right() - 70, area.top() + 130)
    orb_position = overlay.pos()
    assert overlay.is_collapsed()
    assert overlay.size().width() == 58
    assert geometries[-1][2:] == (640, 240)

    qtbot.mouseClick(overlay, Qt.MouseButton.LeftButton)
    assert (
        overlay._geometry_animation.state()
        == QAbstractAnimation.State.Running
    )
    qtbot.waitUntil(lambda: not overlay._animating_geometry, timeout=1000)
    assert not overlay.is_collapsed()
    assert (overlay.width(), overlay.height()) == (640, 240)
    assert overlay.pos() != orb_position

    qtbot.mouseClick(overlay.collapse_button, Qt.MouseButton.LeftButton)
    assert (
        overlay._geometry_animation.state()
        == QAbstractAnimation.State.Running
    )
    qtbot.waitUntil(overlay.is_collapsed, timeout=1000)
    assert overlay.is_collapsed()
    assert overlay.pos() == orb_position


def test_collapsing_running_voice_overlay_pauses_recognition(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(page)
    qtbot.addWidget(overlay)
    repository = _verified_repository()
    capture = _Capture()
    controller = VoiceTranslationController(
        page,
        overlay,
        capture,
        _Speech(),  # type: ignore[arg-type]
        _TranslateVoice(),  # type: ignore[arg-type]
        ManageSettings(repository),
        _Windows(),  # type: ignore[arg-type]
        logging.getLogger("test-voice-collapse-pauses"),
        I18nManager("zh_CN"),
    )
    page.target_combo.setCurrentIndex(page.target_combo.findData(202))
    controller.start()
    qtbot.waitUntil(lambda: capture.running, timeout=3000)
    qtbot.waitUntil(lambda: not overlay._animating_geometry, timeout=1000)

    qtbot.mouseClick(overlay.collapse_button, Qt.MouseButton.LeftButton)

    qtbot.waitUntil(lambda: not capture.running, timeout=1000)
    qtbot.waitUntil(overlay.is_collapsed, timeout=1000)
    assert overlay._recognition_state == "idle"
    controller.shutdown()


def test_voice_overlay_supports_translation_original_and_both_modes(qtbot) -> None:
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(overlay)
    settings = AppSettings().voice.overlay
    overlay.add_caption(VoiceCaption(1, "hello", "你好"))

    settings.display_mode = "translation"
    overlay.apply_settings(settings)
    assert not overlay.findChildren(QLabel, "voiceCaptionOriginal")
    assert overlay.findChildren(QLabel, "voiceCaptionTranslation")

    settings.display_mode = "original"
    overlay.apply_settings(settings)
    assert overlay.findChildren(QLabel, "voiceCaptionOriginal")
    assert not overlay.findChildren(QLabel, "voiceCaptionTranslation")

    settings.display_mode = "both"
    overlay.apply_settings(settings)
    assert overlay.findChildren(QLabel, "voiceCaptionOriginal")
    assert overlay.findChildren(QLabel, "voiceCaptionTranslation")


def test_translation_is_requested_only_after_final_sentence(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(page)
    qtbot.addWidget(overlay)
    capture = _Capture()
    speech = _Speech()
    translator = _RecordingTranslateVoice()
    controller = VoiceTranslationController(
        page,
        overlay,
        capture,
        speech,  # type: ignore[arg-type]
        translator,  # type: ignore[arg-type]
        ManageSettings(_verified_repository()),
        _Windows(),  # type: ignore[arg-type]
        logging.getLogger("test-voice-final-translation"),
        I18nManager("zh_CN"),
    )
    page.target_combo.setCurrentIndex(page.target_combo.findData(202))
    controller.start()
    qtbot.waitUntil(lambda: capture.running, timeout=3000)

    speech.on_event(
        SpeechStreamEvent("partial_transcript", "hello everyone", "sentence-1", "en")
    )

    qtbot.wait(700)
    assert page._partial is True
    assert page._last_original == "hello everyone"
    assert page._last_translated == ""
    assert translator.calls == []
    assert not overlay._captions
    assert overlay._live_caption is not None
    assert overlay._live_caption.translated == ""

    final = SpeechStreamEvent(
        "final_transcript",
        "hello everyone",
        "sentence-1",
        "en",
    )
    speech.on_event(final)
    qtbot.waitUntil(lambda: bool(overlay._captions), timeout=2500)
    assert translator.calls == [("hello everyone", "en")]
    assert overlay._captions[-1].translated == "你好"

    speech.on_event(final)
    qtbot.wait(150)
    assert translator.calls == [("hello everyone", "en")]
    controller.shutdown()


def test_final_translation_failure_is_not_silent(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    overlay = VoiceOverlayWindow(i18n=I18nManager("zh_CN"))
    qtbot.addWidget(page)
    qtbot.addWidget(overlay)
    capture = _Capture()
    speech = _Speech()
    controller = VoiceTranslationController(
        page,
        overlay,
        capture,
        speech,  # type: ignore[arg-type]
        _FailTranslateVoice(),  # type: ignore[arg-type]
        ManageSettings(_verified_repository()),
        _Windows(),  # type: ignore[arg-type]
        logging.getLogger("test-voice-final-error"),
        I18nManager("zh_CN"),
    )
    page.target_combo.setCurrentIndex(page.target_combo.findData(202))
    controller.start()
    qtbot.waitUntil(lambda: capture.running, timeout=3000)

    speech.on_event(
        SpeechStreamEvent("partial_transcript", "hello everyone", "sentence-1", "en")
    )
    qtbot.wait(700)
    assert not overlay.error_label.isVisible()

    speech.on_event(
        SpeechStreamEvent("final_transcript", "hello everyone", "sentence-1", "en")
    )

    qtbot.waitUntil(lambda: overlay.error_label.isVisible(), timeout=2500)
    assert "语音翻译失败" in overlay.error_label.text()
    assert "synthetic translation failure" in overlay.error_label.text()
    controller.shutdown()


def test_voice_service_profiles_delete_legacy_entries(qtbot) -> None:
    page = VoiceSettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    settings = AppSettings()
    settings.voice.asr_profiles = [
        SpeechRecognitionProfile(
            id="realtime",
            name="Tencent ASR",
            provider="tencent_realtime",
            api_key="test-secret",
            model="16k_zh",
            options={"app_id": "123", "secret_id": "id"},
        ),
        SpeechRecognitionProfile(
            id="legacy",
            name="Legacy audio",
            provider="audio_chat_completions",
            base_url="https://api.example/v1",
            api_key="test-key",
            model="audio-model",
        ),
    ]
    settings.voice.asr_profile_id = "realtime"

    page.load_settings(settings)

    assert not page.findChildren(QTreeWidget)
    assert len(page.findChildren(QScrollArea)) == 1
    assert len(page._profile_rows) == 1
    assert page._validation_labels["realtime"].text() == "待验证"
    assert "legacy" not in page._profile_rows
    assert page._profile_rows["realtime"].property("active") is True
    assert not hasattr(page, "test_button")
    assert not hasattr(page, "api_key_edit")
    assert not hasattr(page, "topmost_check")
    assert not hasattr(page, "show_original_check")


def test_add_voice_service_dialog_only_lists_realtime_provider_presets(qtbot) -> None:
    dialog = AddSpeechProfileDialog(I18nManager("zh_CN"))
    qtbot.addWidget(dialog)

    assert {
        dialog.provider_combo.itemData(index)
        for index in range(dialog.provider_combo.count())
    } == {
        "tencent_realtime",
        "aliyun_nls_realtime",
    }
    assert "16k_multi_lang" in {
        dialog.model_combo.itemData(index)
        for index in range(dialog.model_combo.count())
    }


def test_tencent_engine_labels_are_localized_and_custom_values_are_editable(qtbot) -> None:
    expected = {
        "zh_CN": "多语种大模型",
        "en_US": "Multilingual large model",
        "ja_JP": "多言語大規模モデル",
    }
    for locale, label in expected.items():
        dialog = AddSpeechProfileDialog(I18nManager(locale))
        qtbot.addWidget(dialog)
        index = dialog.model_combo.findData("16k_multi_lang")
        assert label in dialog.model_combo.itemText(index)

    profile = SpeechRecognitionProfile(
        id="custom-tencent",
        name="Custom Tencent",
        provider="tencent_realtime",
        api_key="secret-key",
        model="account_custom_engine",
        options={"app_id": "123", "secret_id": "secret-id"},
    )
    dialog = AddSpeechProfileDialog(I18nManager("zh_CN"), profile=profile)
    qtbot.addWidget(dialog)

    assert dialog.model_combo.currentData() == "__custom_tencent_engine__"
    assert dialog.custom_model_edit.isVisibleTo(dialog)
    assert dialog.custom_model_edit.text() == "account_custom_engine"
    assert dialog._profile_values("tencent_realtime")[1] == "account_custom_engine"


def test_voice_profile_rows_use_highlight_and_compact_without_extra_buttons(qtbot) -> None:
    page = VoiceSettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    page.resize(560, 620)
    page.show()
    page.load_settings(AppSettings())
    qtbot.wait(50)

    assert page._profile_rows == {}
    assert page.header.isHidden()

    page.load_settings(_verified_repository().value)
    row = page._profile_rows["speech-test"]
    assert row.property("active") is True
    assert {button.text() for button in row.findChildren(QPushButton)} == {
        "验证通过",
        "编辑",
        "删除",
    }
    assert page.header.isHidden()
    assert (
        page.scroll.horizontalScrollBarPolicy()
        == Qt.ScrollBarPolicy.ScrollBarAlwaysOff
    )


def test_last_voice_profile_can_be_deleted_without_replacement(qtbot) -> None:
    page = VoiceSettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    page.load_settings(_verified_repository().value)

    page._delete_profile("speech-test")
    collected = AppSettings()
    page.collect_settings(collected)

    assert page._profile_rows == {}
    assert page.header.isHidden()
    assert page.validation_notice.isHidden()
    assert collected.voice.asr_profile_id == ""
    assert collected.voice.asr_profiles == []


def test_active_voice_profile_is_auto_saved_and_restored(qtbot) -> None:
    i18n = I18nManager("zh_CN")
    page = SettingsPage(i18n)
    qtbot.addWidget(page)
    repository = _verified_repository()
    second = SpeechRecognitionProfile(
        id="speech-second",
        name="Second ASR",
        provider="aliyun_nls_realtime",
        api_key="synthetic-secret",
        model="nls-realtime",
        options={
            "app_key": "synthetic-app-key",
            "access_key_id": "synthetic-id",
        },
    )
    repository.value.voice.asr_profiles.append(second)
    page.load_settings(repository.value, repository.location)

    def save_page() -> None:
        updated = page.collect_settings(repository.value)
        repository.save(updated)
        page.load_settings(updated, repository.location)

    page.save_requested.connect(save_page)
    page.voice_page._activate_profile(second.id)

    qtbot.waitUntil(
        lambda: repository.value.voice.asr_profile_id == second.id,
        timeout=1000,
    )
    restored = SettingsPage(i18n)
    qtbot.addWidget(restored)
    restored.load_settings(repository.value, repository.location)
    assert restored.voice_page._active_profile_id == second.id
    assert restored.voice_page._profile_rows[second.id].property("active") is True


def test_voice_page_matches_ocr_card_hierarchy(qtbot) -> None:
    page = VoicePage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    page.resize(1200, 760)
    page.show()
    qtbot.wait(50)

    summary_position = page._grid.getItemPosition(
        page._grid.indexOf(page._summary_card)
    )
    tool_position = page._grid.getItemPosition(
        page._grid.indexOf(page._source_card)
    )
    recent_position = page._grid.getItemPosition(
        page._grid.indexOf(page._recent_card)
    )
    overlay_position = page._grid.getItemPosition(
        page._grid.indexOf(page._overlay_card)
    )

    assert summary_position[:2] == (0, 0)
    assert tool_position[:2] == (0, 1)
    assert recent_position == (1, 0, 1, 2)
    assert overlay_position == (2, 0, 1, 2)
    assert page._grid.rowStretch(3) == 1
    assert page._status_label.objectName() == "statusPill"
    assert not hasattr(page, "start_button")
    assert not hasattr(page, "stop_button")


def test_speech_profile_editor_uses_provider_specific_credentials(qtbot) -> None:
    profile = SpeechRecognitionProfile(
        id="tencent",
        name="Tencent",
        provider="tencent_realtime",
        api_key="secret-key",
        model="16k_ja",
        options={"app_id": "123", "secret_id": "secret-id"},
    )
    dialog = AddSpeechProfileDialog(
        I18nManager("zh_CN"),
        profile=profile,
    )
    qtbot.addWidget(dialog)

    assert dialog._field_one_label.text() == "腾讯云 AppID"
    assert dialog._field_two_label.text() == "腾讯云 SecretId"
    assert dialog._field_three_label.text() == "腾讯云 SecretKey"
    assert dialog.field_one_edit.text() == "123"
    assert dialog.field_two_edit.text() == "secret-id"
    assert dialog.field_three_edit.text() == "secret-key"
    assert dialog.model_combo.currentData() == "16k_ja"
    assert "识别语种由识别引擎决定" in dialog.provider_help.text()


def test_voice_detection_settings_explain_effects_and_hide_legacy_guard(qtbot) -> None:
    page = VoiceSettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)

    assert page._segment_title.text() == "人声检测"
    assert page._energy_label.text() == "人声触发阈值"
    assert "数值越低" in page._energy_hint.text()
    assert "连续静音" in page._silence_hint.text()
    assert "字幕出现更慢" in page._minimum_speech_hint.text()
    assert not hasattr(page, "maximum_segment_edit")


def test_voice_profile_validation_result_is_saved_with_profile(qtbot, tmp_path) -> None:
    page = SettingsPage(I18nManager("zh_CN"))
    qtbot.addWidget(page)
    repository = _verified_repository()
    set_profile_validation(
        repository.value.voice.asr_profile(),
        "pending",
        "",
    )
    controller = SettingsController(
        page,
        ManageSettings(repository),
        object(),  # type: ignore[arg-type]
        lambda: None,
        logging.getLogger("test-speech-validation-controller"),
        i18n=I18nManager("zh_CN"),
        speech_validator=_Speech(),  # type: ignore[arg-type]
    )
    page.voice_page._validation_labels["speech-test"].click()
    qtbot.waitUntil(
        lambda: page.voice_page._validation_labels["speech-test"].text()
        == "验证通过",
        timeout=3000,
    )
    page.save_requested.emit()

    assert profile_validation_state(repository.value.voice.asr_profile()) == "verified"
    assert not page.has_unsaved_changes
    del controller
