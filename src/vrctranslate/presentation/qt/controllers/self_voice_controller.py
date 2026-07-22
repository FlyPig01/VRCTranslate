from __future__ import annotations

import logging
from array import array
from collections import deque
from copy import deepcopy
from threading import RLock

from PySide6.QtCore import QObject, QThreadPool, QTimer, Signal

from vrctranslate.application.dto import AppSettings
from vrctranslate.application.ports.microphone_capture import MicrophoneCapture
from vrctranslate.application.ports.speech_recognizer import SpeechRecognizer
from vrctranslate.application.ports.window_catalog import WindowCatalog
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.recognize_self_voice_segment import (
    RecognizeSelfVoiceSegment,
    self_voice_profile,
)
from vrctranslate.application.use_cases.voice_segmenter import VoiceSegmenter
from vrctranslate.domain.speech import (
    AudioFrame,
    MicrophoneCaptureError,
    SpeechRecognitionError,
    SpeechRecognitionResult,
)
from vrctranslate.presentation.qt.controllers.self_message_controller import (
    SelfMessageController,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker


class SelfVoiceController(QObject):
    """Automatically recognize microphone sentences and submit them to OSC text."""

    status_bar_message = Signal(str, int)
    hotkeys_changed = Signal()
    _capture_failed = Signal(object)
    _test_capture_failed = Signal(object)
    _segment_ready = Signal(object)
    _level_ready = Signal(int)

    def __init__(
        self,
        page: SelfMessagePage,
        capture: MicrophoneCapture,
        speech: SpeechRecognizer,
        settings: ManageSettings,
        windows: WindowCatalog,
        self_messages: SelfMessageController,
        logger: logging.Logger,
        i18n: I18nManager,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._capture = capture
        self._speech = speech
        self._settings = settings
        self._windows = windows
        self._self_messages = self_messages
        self._logger = logger
        self._i18n = i18n
        self._recognize = RecognizeSelfVoiceSegment(speech)
        self._lock = RLock()
        self._segmenter: VoiceSegmenter | None = None
        self._recognition_worker: TaskWorker | None = None
        self._validation_worker: TaskWorker | None = None
        self._pending_segments: deque[bytes] = deque()
        self._validated = False
        self._blocked = False
        self._shutting_down = False
        self._generation = 0
        self._model_released = False
        self._testing_microphone = False
        self._test_peak = 0
        self._voice_translation_active = False
        self._settings_signature: tuple[object, ...] = ()

        self._poll_timer = QTimer(self)
        self._poll_timer.setInterval(750)
        self._poll_timer.timeout.connect(self._evaluate_capture)
        self._poll_timer.start()
        self._save_timer = QTimer(self)
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(350)
        self._save_timer.timeout.connect(self._save_settings)
        self._microphone_test_timer = QTimer(self)
        self._microphone_test_timer.setSingleShot(True)
        self._microphone_test_timer.setInterval(5000)
        self._microphone_test_timer.timeout.connect(self._finish_microphone_test)
        self._calibration_timer = QTimer(self)
        self._calibration_timer.setSingleShot(True)
        self._calibration_timer.setInterval(600)
        self._calibration_timer.timeout.connect(self._calibration_finished)
        self._translation_status_timer = QTimer(self)
        self._translation_status_timer.setSingleShot(True)
        self._translation_status_timer.setInterval(1200)
        self._translation_status_timer.timeout.connect(
            self._restore_listening_status
        )

        page.self_voice_settings_changed.connect(self._settings_edited)
        page.microphone_test_requested.connect(self._toggle_microphone_test)
        translation_status = getattr(
            self_messages,
            "voice_translation_status",
            None,
        )
        if translation_status is not None:
            translation_status.connect(self._voice_translation_status_changed)
        self._capture_failed.connect(self._on_capture_failed)
        self._test_capture_failed.connect(self._on_test_capture_failed)
        self._segment_ready.connect(self._recognize_segment)
        self._level_ready.connect(page.set_microphone_level)
        self.apply_settings(settings.current)

    def apply_settings(self, settings: object) -> None:
        if not isinstance(settings, AppSettings):
            return
        current = settings.self_voice
        signature = (
            current.enabled,
            current.microphone_id,
            current.source_language,
            current.activation_scope,
            current.segment.energy_threshold,
            current.segment.silence_ms,
            current.segment.minimum_speech_ms,
            current.segment.maximum_segment_seconds,
        )
        changed = signature != self._settings_signature
        self._settings_signature = signature
        self._page.load_self_voice_settings(current)
        self.refresh_microphones()
        if changed:
            self._blocked = False
            if self._capture.running:
                self._stop_capture()
        QTimer.singleShot(0, self._evaluate_capture)

    def refresh_microphones(self) -> None:
        try:
            devices = self._capture.list_devices()
            selected = self._settings.current.self_voice.microphone_id
            resolved = self._capture.resolve_device_id(selected)
            if selected and resolved != selected:
                self._settings.current.self_voice.microphone_id = resolved
                self._save_timer.start()
        except MicrophoneCaptureError as exc:
            devices = []
            resolved = self._settings.current.self_voice.microphone_id
            self._set_status(str(exc), "error")
        self._page.set_microphone_devices(
            devices,
            resolved,
        )

    def shutdown(self) -> None:
        self._shutting_down = True
        self._poll_timer.stop()
        self._save_timer.stop()
        self._microphone_test_timer.stop()
        self._calibration_timer.stop()
        self._translation_status_timer.stop()
        self._testing_microphone = False
        self._page.set_microphone_test_running(False)
        self._stop_capture()
        self._pending_segments.clear()
        self._release_model_if_idle()

    def _settings_edited(
        self,
        enabled: bool,
        microphone_id: str,
        source_language: str,
        activation_scope: str,
        toggle_hotkey: str,
    ) -> None:
        if self._testing_microphone:
            self._finish_microphone_test(resume=False)
        current = self._settings.current.self_voice
        current.enabled = enabled
        current.microphone_id = microphone_id
        current.source_language = source_language
        current.activation_scope = activation_scope
        hotkey_changed = current.toggle_hotkey != toggle_hotkey
        current.toggle_hotkey = toggle_hotkey
        self._blocked = False
        self._save_timer.start()
        self.apply_settings(self._settings.current)
        if hotkey_changed:
            self.hotkeys_changed.emit()

    def toggle_enabled(self) -> None:
        current = self._settings.current.self_voice
        self._settings_edited(
            not current.enabled,
            current.microphone_id,
            current.source_language,
            current.activation_scope,
            current.toggle_hotkey,
        )

    def _save_settings(self) -> None:
        try:
            self._settings.save(self._settings.current)
        except OSError as exc:
            self.status_bar_message.emit(
                self._i18n.tr("ctrl.settings.save_failed", error=str(exc)), 6000
            )

    def _evaluate_capture(self) -> None:
        if self._shutting_down:
            return
        if self._testing_microphone:
            return
        config = self._settings.current.self_voice
        if not config.enabled:
            self._stop_capture()
            self._set_status(self._i18n.tr("self_voice.status_disabled"), "idle")
            return
        if not self._scope_is_active(config.activation_scope):
            self._stop_capture()
            self._set_status(self._i18n.tr("self_voice.status_waiting_vrchat"), "idle")
            return
        if self._blocked:
            return
        if self._capture.running or self._validation_worker is not None:
            return
        if not self._validated:
            self._validate_model()
            return
        self._start_capture()

    def _validate_model(self) -> None:
        self._set_status(
            self._i18n.tr("self_voice.status_checking_model"),
            "recognizing",
        )
        worker = TaskWorker(lambda: self._speech.validate_profile(self_voice_profile()))
        self._validation_worker = worker
        worker.signals.succeeded.connect(self._model_validated)
        worker.signals.failed.connect(self._model_validation_failed)
        worker.signals.finished.connect(lambda: self._clear_validation_worker(worker))
        QThreadPool.globalInstance().start(worker)

    def _model_validated(self, result: object) -> None:
        if self._shutting_down:
            return
        if getattr(result, "state", "failed") != "verified":
            self._blocked = True
            self._set_status(
                str(getattr(result, "message", "SenseVoice本地模型尚未安装")),
                "error",
            )
            return
        self._validated = True
        QTimer.singleShot(0, self._evaluate_capture)

    def _model_validation_failed(self, error: object) -> None:
        if self._shutting_down:
            return
        self._blocked = True
        self._set_status(
            getattr(error, "user_message", str(error))
            or self._i18n.tr("self_voice.error_model"),
            "error",
        )

    def _clear_validation_worker(self, worker: TaskWorker) -> None:
        if self._validation_worker is worker:
            self._validation_worker = None
        self._release_model_if_idle()

    def _start_capture(self) -> None:
        with self._lock:
            self._segmenter = VoiceSegmenter(
                self._settings.current.self_voice.segment,
                adaptive_noise=True,
                calibration_ms=500,
            )
        try:
            self._capture.start(
                self._settings.current.self_voice.microphone_id,
                self._on_audio_frame,
                on_error=self._capture_failed.emit,
            )
        except MicrophoneCaptureError as exc:
            self._blocked = True
            self._set_status(str(exc), "error")
            return
        self._set_status(
            self._i18n.tr("self_voice.status_calibrating"),
            "calibrating",
        )
        self._calibration_timer.start()
        self._logger.info("self_voice_microphone_started")

    def _stop_capture(self) -> None:
        self._calibration_timer.stop()
        self._translation_status_timer.stop()
        self._voice_translation_active = False
        if (
            self._capture.running
            or self._segmenter is not None
            or self._recognition_worker is not None
            or self._pending_segments
        ):
            self._generation += 1
        if self._capture.running:
            self._capture.stop()
            self._logger.info("self_voice_microphone_stopped")
        with self._lock:
            if self._segmenter is not None:
                self._segmenter.reset()
            self._segmenter = None
        self._pending_segments.clear()
        self._page.set_microphone_level(0)

    def _calibration_finished(self) -> None:
        if self._capture.running and not self._testing_microphone:
            self._set_status(
                self._i18n.tr("self_voice.status_listening"),
                "listening",
            )

    def _toggle_microphone_test(self) -> None:
        if self._testing_microphone:
            self._finish_microphone_test()
            return
        self._stop_capture()
        self._testing_microphone = True
        self._test_peak = 0
        self._page.set_microphone_test_running(True)
        try:
            self._capture.start(
                self._settings.current.self_voice.microphone_id,
                self._on_test_frame,
                on_error=self._test_capture_failed.emit,
            )
        except MicrophoneCaptureError as exc:
            self._testing_microphone = False
            self._page.set_microphone_test_running(False)
            self._set_status(str(exc), "error")
            return
        self._set_status(
            self._i18n.tr(
                "self_voice.test_listening",
                phrase=self._page.microphone_test_phrase(),
            ),
            "testing",
        )
        self._microphone_test_timer.start()

    def _on_test_frame(self, frame: AudioFrame) -> None:
        amplitude = _mean_absolute_amplitude(frame.pcm16)
        self._test_peak = max(self._test_peak, amplitude)
        self._level_ready.emit(amplitude)

    def _finish_microphone_test(self, *, resume: bool = True) -> None:
        if not self._testing_microphone:
            return
        self._microphone_test_timer.stop()
        self._testing_microphone = False
        self._page.set_microphone_test_running(False)
        self._stop_capture()
        if self._test_peak >= 80:
            message = self._i18n.tr("self_voice.test_success")
            state = "success"
        else:
            message = self._i18n.tr("self_voice.test_no_sound")
            state = "error"
        self._set_status(message, state)
        if resume and not self._shutting_down:
            QTimer.singleShot(900, self._evaluate_capture)

    def _on_test_capture_failed(self, error: MicrophoneCaptureError) -> None:
        self._microphone_test_timer.stop()
        self._testing_microphone = False
        self._page.set_microphone_test_running(False)
        self._stop_capture()
        self._set_status(str(error), "error")

    def _on_audio_frame(self, frame: AudioFrame) -> None:
        self._level_ready.emit(_mean_absolute_amplitude(frame.pcm16))
        try:
            with self._lock:
                segmenter = self._segmenter
                segment = segmenter.feed(frame) if segmenter is not None else None
            if segment:
                self._segment_ready.emit(segment)
        except Exception as exc:
            self._capture_failed.emit(MicrophoneCaptureError(str(exc)))

    def _recognize_segment(self, value: object) -> None:
        if not isinstance(value, bytes) or not value:
            return
        maximum = max(1, self._settings.current.self_voice.queue_limit)
        if self._recognition_worker is not None:
            if len(self._pending_segments) >= maximum:
                self._set_status(
                    self._i18n.tr("self_voice.error_queue_full"), "error"
                )
                self._logger.warning("self_voice_segment_rejected reason=queue_full")
                return
            self._pending_segments.append(value)
            return
        snapshot = deepcopy(self._settings.current)
        generation = self._generation
        worker = TaskWorker(lambda: self._recognize.execute(value, snapshot))
        self._recognition_worker = worker
        worker.signals.succeeded.connect(
            lambda result: self._segment_recognized(result, generation)
        )
        worker.signals.failed.connect(
            lambda error: self._recognition_failed(error, generation)
        )
        worker.signals.finished.connect(lambda: self._recognition_finished(worker))
        self._set_status(
            self._i18n.tr("self_voice.status_recognizing"),
            "recognizing",
        )
        QThreadPool.globalInstance().start(worker)

    def _segment_recognized(self, value: object, generation: int) -> None:
        if self._shutting_down or generation != self._generation:
            return
        if not isinstance(value, SpeechRecognitionResult):
            return
        original = value.text.strip()
        if not original:
            self._set_status(
                self._i18n.tr("self_voice.status_listening"),
                "listening",
            )
            return
        self._page.set_self_voice_original(original)
        if self._self_messages.submit_voice_text(
            original,
            value.detected_language,
        ):
            self._voice_translation_active = True
            self._set_status(
                self._i18n.tr("self_voice.status_translating"),
                "translating",
            )
        else:
            self._set_status(self._i18n.tr("self_voice.error_queue_full"), "error")

    def _recognition_failed(self, error: object, generation: int) -> None:
        if self._shutting_down or generation != self._generation:
            return
        if isinstance(error, ValueError) and "空文本" in str(error):
            return
        message = (
            getattr(error, "user_message", str(error))
            if isinstance(error, SpeechRecognitionError)
            else self._i18n.tr("self_voice.error_recognition")
        )
        self._set_status(message, "error")
        self._logger.warning("self_voice_recognition_failed error=%s", type(error).__name__)

    def _recognition_finished(self, worker: TaskWorker) -> None:
        if self._recognition_worker is worker:
            self._recognition_worker = None
        if (
            not self._shutting_down
            and self._capture.running
            and self._pending_segments
        ):
            self._recognize_segment(self._pending_segments.popleft())
        elif (
            not self._shutting_down
            and self._capture.running
            and not self._voice_translation_active
        ):
            self._set_status(
                self._i18n.tr("self_voice.status_listening"),
                "listening",
            )
        self._release_model_if_idle()

    def _voice_translation_status_changed(self, message: str, state: str) -> None:
        if (
            self._shutting_down
            or not self._settings.current.self_voice.enabled
            or not self._capture.running
        ):
            return
        self._translation_status_timer.stop()
        self._voice_translation_active = state == "translating"
        self._set_status(message, state)
        if state in {"success", "error"}:
            self._translation_status_timer.start()

    def _restore_listening_status(self) -> None:
        if (
            not self._shutting_down
            and self._capture.running
            and not self._testing_microphone
        ):
            self._voice_translation_active = False
            self._set_status(
                self._i18n.tr("self_voice.status_listening"),
                "listening",
            )

    def _on_capture_failed(self, error: object) -> None:
        self._stop_capture()
        self._blocked = True
        self._set_status(
            str(error) or self._i18n.tr("self_voice.error_capture"), "error"
        )
        self._logger.warning("self_voice_capture_failed error=%s", type(error).__name__)

    def _scope_is_active(self, scope: str) -> bool:
        if scope == "always":
            return True
        try:
            targets = [
                window
                for window in self._windows.list_windows()
                if window.process_name.casefold() == "vrchat.exe"
            ]
        except Exception:
            return False
        if not targets:
            return False
        if scope == "vrchat_running":
            return True
        checker = getattr(self._windows, "is_foreground_window", None)
        return bool(callable(checker) and any(checker(item.hwnd) for item in targets))

    def _set_status(self, message: str, state: str) -> None:
        self._page.set_self_voice_status(message, state)
        if state == "error":
            self.status_bar_message.emit(message, 7000)

    def _release_model_if_idle(self) -> None:
        if (
            not self._shutting_down
            or self._model_released
            or self._recognition_worker is not None
            or self._validation_worker is not None
        ):
            return
        try:
            self._speech.release(self_voice_profile())
        except Exception:
            pass
        self._model_released = True


def _mean_absolute_amplitude(pcm16: bytes) -> int:
    samples = array("h")
    samples.frombytes(pcm16[: len(pcm16) - len(pcm16) % 2])
    if not samples:
        return 0
    return int(sum(abs(value) for value in samples) / len(samples))
