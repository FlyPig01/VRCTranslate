from __future__ import annotations

import logging
import threading
from collections import deque
from copy import deepcopy

from PySide6.QtCore import QObject, QThreadPool, QTimer, Signal

from vrctranslate.application.dto import AppSettings, VoiceOverlaySettings
from vrctranslate.application.ports.process_audio_capture import ProcessAudioCapture
from vrctranslate.application.ports.speech_recognizer import (
    SpeechRecognizer,
    SpeechStreamSession,
)
from vrctranslate.application.ports.window_catalog import WindowCatalog
from vrctranslate.application.speech_profiles import profile_validation_state
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.recognize_voice_segment import (
    RecognizeVoiceSegment,
)
from vrctranslate.application.use_cases.translate_voice_text import TranslateVoiceText
from vrctranslate.application.use_cases.voice_activity_gate import VoiceActivityGate
from vrctranslate.application.use_cases.voice_segmenter import VoiceSegmenter
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.ocr import WindowInfo
from vrctranslate.domain.speech import (
    AudioFrame,
    ProcessAudioCaptureError,
    SpeechRecognitionError,
    SpeechRecognitionResult,
    SpeechStreamConfig,
    SpeechStreamEvent,
    VoiceCaption,
)
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.voice_page import VoicePage
from vrctranslate.presentation.qt.windows.voice_overlay_window import VoiceOverlayWindow
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker


class VoiceTranslationController(QObject):
    """Run a cancellable realtime process-audio and translation session."""

    status_bar_message = Signal(str, int)
    _capture_failed = Signal(int, object)
    _speech_event_ready = Signal(int, object)
    _speech_failed = Signal(int, object)
    _segment_ready = Signal(int, object)

    def __init__(
        self,
        page: VoicePage,
        overlay: VoiceOverlayWindow,
        capture: ProcessAudioCapture,
        speech: SpeechRecognizer,
        translate_voice: TranslateVoiceText,
        settings: ManageSettings,
        windows_api: WindowCatalog,
        logger: logging.Logger,
        i18n: I18nManager,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._overlay = overlay
        self._capture = capture
        self._speech = speech
        self._recognize_segment = RecognizeVoiceSegment(speech)
        self._translate_voice = translate_voice
        self._settings = settings
        self._windows_api = windows_api
        self._logger = logger
        self._i18n = i18n
        self._session = 0
        self._sequence = 0
        self._next_caption_sequence = 1
        self._completed: dict[int, VoiceCaption | None] = {}
        self._capturing = False
        self._starting = False
        self._speech_session: SpeechStreamSession | None = None
        self._gate: VoiceActivityGate | None = None
        self._segmenter: VoiceSegmenter | None = None
        self._recognition_mode = "streaming"
        self._stream_lock = threading.Lock()
        self._start_worker: TaskWorker | None = None
        self._recognition_worker: TaskWorker | None = None
        self._pending_segments: deque[tuple[int, bytes]] = deque()
        self._workers: dict[tuple[int, int], TaskWorker] = {}
        self._pending_translation: tuple[int, str, str] | None = None
        self._selected_process_id: int | None = None
        self._applying_overlay = False
        self._finalized_utterances: dict[str, str] = {}
        self._partial_original = ""

        self._partial_timer = QTimer(self)
        self._partial_timer.setSingleShot(True)
        self._partial_timer.setInterval(80)
        self._partial_timer.timeout.connect(self._render_partial)
        self._overlay_save_timer = QTimer(self)
        self._overlay_save_timer.setSingleShot(True)
        self._overlay_save_timer.setInterval(350)
        self._overlay_save_timer.timeout.connect(self._save_overlay_settings)

        page.refresh_targets_requested.connect(self.refresh_targets)
        page.target_selected.connect(self._target_selected)
        page.overlay_show_requested.connect(overlay.show_overlay)
        page.overlay_clear_requested.connect(self._clear_captions)
        page.overlay_reset_requested.connect(self._reset_overlay)
        page.overlay_settings_changed.connect(self._preview_overlay_settings)
        overlay.recognition_toggle_requested.connect(self._toggle_recognition)
        overlay.geometry_changed.connect(self._save_overlay_geometry)
        self._capture_failed.connect(self._on_capture_failed)
        self._speech_event_ready.connect(self._on_speech_event)
        self._speech_failed.connect(self._on_speech_failed)
        self._segment_ready.connect(self._recognize_completed_segment)
        self.apply_settings(settings.current)
        self._overlay.set_recognition_running(False)
        self.refresh_targets()

    def refresh_targets(self) -> None:
        listed = self._windows_api.list_windows()
        unique: dict[int, WindowInfo] = {}
        for window in listed:
            if window.process_id > 0 and window.process_id not in unique:
                unique[window.process_id] = window
        windows = list(unique.values())
        available = {window.process_id for window in windows}
        selected = self._selected_process_id if self._selected_process_id in available else None
        if selected is None:
            saved_name = self._settings.current.voice.target_process_name.casefold()
            saved_title = self._settings.current.voice.target_window_title.casefold()
            name_matches = [
                window for window in windows
                if window.process_name.casefold() == saved_name
            ]
            title_matches = [
                window for window in name_matches
                if saved_title and window.title.casefold() == saved_title
            ]
            if len(title_matches) == 1:
                selected = title_matches[0].process_id
            elif len(name_matches) == 1:
                selected = name_matches[0].process_id
        self._selected_process_id = selected
        self._page.set_target_windows(windows, selected)

    def start(self) -> None:
        if self._capturing or self._starting:
            return
        self._overlay.clear_error()
        process_id = self._page.selected_process_id
        if process_id is None:
            self._show_error(self._i18n.tr("voice.error_select_process"))
            return
        try:
            self._validate_services(self._settings.current)
        except (ValueError, SpeechRecognitionError) as exc:
            self._show_error(getattr(exc, "user_message", str(exc)))
            return

        self._session += 1
        session = self._session
        self._sequence = 0
        self._next_caption_sequence = 1
        self._completed.clear()
        self._pending_translation = None
        self._finalized_utterances.clear()
        self._partial_original = ""
        profile = self._settings.current.voice.asr_profile()
        self._recognition_mode = self._speech.capabilities(profile).recognition_mode
        with self._stream_lock:
            if self._recognition_mode == "segmented":
                self._segmenter = VoiceSegmenter(self._settings.current.voice.segment)
                self._gate = None
            else:
                self._gate = VoiceActivityGate(self._settings.current.voice.segment)
                self._segmenter = None
        self._starting = True
        self._overlay.set_recognition_state("starting")
        self._page.set_running(False, starting=True)
        self._page.set_status("starting")
        snapshot = deepcopy(self._settings.current)
        worker = TaskWorker(
            lambda: self._start_pipeline(session, process_id, snapshot)
        )
        self._start_worker = worker
        worker.signals.succeeded.connect(
            lambda result: self._pipeline_started(session, result)
        )
        worker.signals.failed.connect(
            lambda error: self._pipeline_start_failed(session, error)
        )
        worker.signals.finished.connect(lambda: self._clear_start_worker(worker))
        QThreadPool.globalInstance().start(worker)

    def _toggle_recognition(self) -> None:
        if self._capturing or self._starting:
            self.stop()
        else:
            self.start()

    def _start_pipeline(
        self,
        session: int,
        process_id: int,
        settings: AppSettings,
    ) -> SpeechStreamSession | None:
        profile = settings.voice.asr_profile()
        capabilities = self._speech.capabilities(profile)
        if capabilities.recognition_mode == "segmented":
            result = self._speech.validate_profile(profile)
            if result.state != "verified":
                raise SpeechRecognitionError("model_missing", result.message)
            self._capture.start(
                process_id,
                lambda frame: self._on_audio_frame(session, frame),
                include_process_tree=True,
                on_error=lambda error: self._capture_failed.emit(session, error),
            )
            if session != self._session:
                self._capture.stop()
                raise SpeechRecognitionError("cancelled", "语音会话已取消")
            return None
        route = settings.translation.voice_route
        stream = self._speech.open_session(
            profile,
            SpeechStreamConfig(route.source_language, route.target_language),
            lambda event: self._speech_event_ready.emit(session, event),
            lambda error: self._speech_failed.emit(session, error),
        )
        with self._stream_lock:
            if session != self._session:
                stream.cancel()
                raise SpeechRecognitionError("cancelled", "语音会话已取消")
            self._speech_session = stream
        try:
            self._capture.start(
                process_id,
                lambda frame: self._on_audio_frame(session, frame),
                include_process_tree=True,
                on_error=lambda error: self._capture_failed.emit(session, error),
            )
            if session != self._session:
                self._capture.stop()
                stream.cancel()
                raise SpeechRecognitionError("cancelled", "语音会话已取消")
        except Exception:
            stream.cancel()
            with self._stream_lock:
                if self._speech_session is stream:
                    self._speech_session = None
            raise
        return stream

    def stop(self) -> None:
        if not self._capturing and not self._starting:
            return
        was_capturing = self._capturing
        self._session += 1
        self._starting = False
        self._capturing = False
        self._partial_timer.stop()
        release_profile = None
        if was_capturing and self._recognition_mode == "segmented":
            try:
                release_profile = self._settings.current.voice.asr_profile()
            except (ValueError, KeyError):
                pass
        with self._stream_lock:
            stream, self._speech_session = self._speech_session, None
            if self._gate is not None:
                self._gate.reset()
            self._gate = None
            if self._segmenter is not None:
                self._segmenter.reset()
            self._segmenter = None
        self._completed.clear()
        self._pending_segments.clear()
        self._pending_translation = None
        self._finalized_utterances.clear()
        self._capture.stop()
        if stream is not None:
            stream.cancel()
        if release_profile is not None:
            self._speech.release(release_profile)
        self._overlay.set_live_caption("", "")
        self._overlay.set_recognition_running(False)
        self._page.set_running(False)
        self._page.set_status("idle")
        self._logger.info("voice_capture_stopped")

    def apply_settings(self, settings: object) -> None:
        if not isinstance(settings, AppSettings):
            return
        self._page.load_settings(settings)
        self._applying_overlay = True
        try:
            self._overlay.apply_settings(settings.voice.overlay)
        finally:
            self._applying_overlay = False

    def shutdown(self) -> None:
        self.stop()
        if self._overlay_save_timer.isActive():
            self._overlay_save_timer.stop()
            self._save_overlay_settings()
        self._overlay.close_permanently()

    def _pipeline_started(self, session: int, result: object) -> None:
        if session != self._session or not self._starting:
            if hasattr(result, "cancel"):
                result.cancel()
            self._capture.stop()
            return
        self._starting = False
        self._capturing = True
        self._overlay.set_recognition_running(True)
        self._page.set_running(True)
        self._page.set_status("listening")
        self._logger.info(
            "voice_capture_started process_id=%d recognition_mode=%s",
            self._selected_process_id or 0,
            self._recognition_mode,
        )

    def _pipeline_start_failed(self, session: int, error: object) -> None:
        if session != self._session:
            return
        self._starting = False
        self._capturing = False
        self._overlay.set_recognition_running(False)
        self._page.set_running(False)
        if isinstance(error, (ProcessAudioCaptureError, SpeechRecognitionError)):
            message = getattr(error, "user_message", str(error))
        else:
            message = self._i18n.tr("voice.error_capture_start")
        self._show_error(message)
        self._logger.warning(
            "voice_stream_start_failed error=%s", type(error).__name__
        )

    def _on_capture_failed(self, session: int, error: object) -> None:
        if session != self._session:
            return
        self.stop()
        self._show_error(str(error) or self._i18n.tr("voice.error_capture_stopped"))
        self._logger.warning("voice_capture_failed error=%s", type(error).__name__)

    def _on_audio_frame(self, session: int, frame: AudioFrame) -> None:
        if session != self._session:
            return
        try:
            with self._stream_lock:
                segmenter = self._segmenter
                gate = self._gate
                stream = self._speech_session
                if segmenter is not None:
                    segment = segmenter.feed(frame)
                    frames = ()
                else:
                    segment = None
                    frames = gate.feed(frame) if gate is not None else ()
            if segment is not None:
                self._segment_ready.emit(session, segment)
                return
            if stream is None:
                return
            for forwarded in frames:
                stream.push_audio(forwarded)
        except Exception as exc:
            self._speech_failed.emit(session, exc)

    def _recognize_completed_segment(self, session: int, value: object) -> None:
        if session != self._session or not isinstance(value, bytes) or not value:
            return
        if self._recognition_worker is not None:
            limit = max(1, self._settings.current.translation.voice_route.queue_limit)
            while len(self._pending_segments) >= limit:
                self._pending_segments.popleft()
                self._logger.info("voice_segment_dropped reason=recognition_queue_full")
            self._pending_segments.append((session, value))
            return
        snapshot = deepcopy(self._settings.current)
        worker = TaskWorker(lambda: self._recognize_segment.execute(value, snapshot))
        self._recognition_worker = worker
        worker.signals.succeeded.connect(
            lambda result: self._segment_recognized(session, result)
        )
        worker.signals.failed.connect(
            lambda error: self._segment_recognition_failed(session, error)
        )
        worker.signals.finished.connect(
            lambda current=worker: self._recognition_finished(current)
        )
        self._page.set_status("recognizing")
        QThreadPool.globalInstance().start(worker)

    def _segment_recognized(self, session: int, value: object) -> None:
        if session != self._session or not isinstance(value, SpeechRecognitionResult):
            return
        original = value.text.strip()
        if not original:
            self._page.set_status("listening")
            return
        self._partial_original = original
        self._render_partial()
        self._queue_translation(session, original, value.detected_language)

    def _segment_recognition_failed(self, session: int, error: object) -> None:
        if session != self._session:
            return
        if isinstance(error, ValueError) and "空文本" in str(error):
            return
        self._on_speech_failed(session, error)

    def _recognition_finished(self, worker: TaskWorker) -> None:
        if self._recognition_worker is worker:
            self._recognition_worker = None
        while self._pending_segments:
            pending = self._pending_segments.popleft()
            if pending[0] == self._session:
                self._recognize_completed_segment(*pending)
                break

    def _on_speech_event(self, session: int, value: object) -> None:
        if session != self._session or not isinstance(value, SpeechStreamEvent):
            return
        if value.kind == "partial_transcript":
            original = value.text.strip()
            if not original:
                return
            self._partial_original = original
            self._partial_timer.start()
            return
        if value.kind == "final_transcript":
            original = value.text.strip()
            if not original:
                return
            utterance_id = value.utterance_id.strip()
            if utterance_id and utterance_id != "current":
                if self._finalized_utterances.get(utterance_id) == original:
                    self._logger.debug(
                        "voice_translation_skipped reason=duplicate_final"
                    )
                    return
                self._finalized_utterances[utterance_id] = original
            self._partial_original = original
            self._queue_translation(session, original, value.detected_language)
            return

    def _render_partial(self) -> None:
        if not self._partial_original:
            return
        self._page.set_partial_caption(
            self._partial_original,
            "",
        )
        self._overlay.set_live_caption(
            self._partial_original,
            "",
        )
        if not self._overlay.isVisible():
            self._overlay.show_overlay()
        self._page.set_status("recognizing")

    def _queue_translation(
        self,
        session: int,
        original: str,
        detected_language: str,
    ) -> None:
        self._render_partial()
        limit = self._settings.current.translation.voice_route.queue_limit
        if sum(key[0] == session for key in self._workers) >= limit:
            self._pending_translation = (
                session,
                original,
                detected_language,
            )
            self._logger.info(
                "voice_translation_deferred reason=queue_full policy=latest"
            )
            return
        self._sequence += 1
        sequence = self._sequence
        snapshot = deepcopy(self._settings.current)
        worker = TaskWorker(
            lambda: self._translate_voice.execute(
                original,
                detected_language,
                sequence,
                snapshot,
            )
        )
        key = (session, sequence)
        self._workers[key] = worker
        worker.signals.succeeded.connect(
            lambda result: self._caption_ready(session, result)
        )
        worker.signals.failed.connect(
            lambda error: self._translation_failed(session, sequence, error)
        )
        worker.signals.finished.connect(lambda: self._worker_finished(key))
        QThreadPool.globalInstance().start(worker)

    def _caption_ready(self, session: int, result: object) -> None:
        if session != self._session or not isinstance(result, VoiceCaption):
            return
        self._completed[result.sequence] = result
        self._drain_completed()

    def _translation_failed(self, session: int, sequence: int, error: object) -> None:
        if session != self._session:
            return
        self._completed[sequence] = None
        self._drain_completed()
        message = (
            getattr(error, "user_message", str(error))
            if isinstance(error, (SpeechRecognitionError, TranslationError, ValueError))
            else self._i18n.tr("voice.error_processing")
        )
        self._show_error(message)
        self._logger.warning("voice_translation_failed error=%s", type(error).__name__)

    def _drain_completed(self) -> None:
        while self._next_caption_sequence in self._completed:
            sequence = self._next_caption_sequence
            result = self._completed.pop(sequence)
            self._next_caption_sequence += 1
            if result is None:
                continue
            self._page.set_last_caption(result.original, result.translated)
            self._overlay.clear_error()
            self._overlay.add_caption(result)
            if not self._overlay.isVisible():
                self._overlay.show_overlay()
            if self._partial_original == result.original:
                self._partial_original = ""
            self._page.set_status("listening")
            self._logger.info("voice_caption_ready sequence=%d", result.sequence)

    def _worker_finished(self, key: tuple[int, int]) -> None:
        self._workers.pop(key, None)
        pending = self._pending_translation
        if pending is not None and pending[0] == self._session:
            self._pending_translation = None
            self._queue_translation(*pending)

    def _on_speech_failed(self, session: int, error: object) -> None:
        if session != self._session:
            return
        message = getattr(error, "user_message", str(error))
        self.stop()
        self._show_error(message or self._i18n.tr("voice.error_processing"))
        self._logger.warning("voice_stream_failed error=%s", type(error).__name__)

    def _target_selected(self, process_id: int) -> None:
        self._selected_process_id = process_id
        window = self._page.selected_window()
        if window is None:
            return
        voice = self._settings.current.voice
        voice.target_process_name = window.process_name
        voice.target_window_title = window.title
        self._settings.save(self._settings.current)

    def _preview_overlay_settings(
        self,
        topmost: bool,
        display_mode: str,
        opacity: float,
        font_size: int,
        max_items: int,
    ) -> None:
        overlay = self._settings.current.voice.overlay
        overlay.topmost = topmost
        overlay.display_mode = (
            display_mode
            if display_mode in {"translation", "original", "both"}
            else "both"
        )
        overlay.show_original = overlay.display_mode in {"original", "both"}
        overlay.opacity = opacity
        overlay.font_size = font_size
        overlay.max_items = max_items
        self._applying_overlay = True
        try:
            self._overlay.apply_settings(overlay)
        finally:
            self._applying_overlay = False
        self._overlay_save_timer.start()

    def _save_overlay_settings(self) -> None:
        try:
            self._settings.save(self._settings.current)
        except OSError as exc:
            self.status_bar_message.emit(
                self._i18n.tr("ctrl.settings.save_failed", error=str(exc)),
                6000,
            )

    def _save_overlay_geometry(
        self, x: int, y: int, width: int, height: int
    ) -> None:
        if self._applying_overlay:
            return
        overlay = self._settings.current.voice.overlay
        overlay.x, overlay.y = x, y
        overlay.width, overlay.height = width, height
        self._overlay_save_timer.start()

    def _reset_overlay(self) -> None:
        overlay = self._settings.current.voice.overlay
        defaults = VoiceOverlaySettings()
        overlay.x, overlay.y = -1, -1
        overlay.width, overlay.height = defaults.width, defaults.height
        self._overlay.reset_geometry(overlay)
        self._page.load_settings(self._settings.current)
        self._overlay_save_timer.start()

    def _clear_captions(self) -> None:
        self._overlay.clear_captions()
        self._page.set_last_caption("", "")

    def _show_error(self, message: str) -> None:
        detailed = self._i18n.tr("voice.error_with_action", reason=message)
        self._page.set_status(detailed)
        self._overlay.show_error(detailed)
        self.status_bar_message.emit(detailed, 8000)

    def _validate_services(self, settings: AppSettings) -> None:
        profile = settings.voice.asr_profile()
        capabilities = self._speech.capabilities(profile)
        if not capabilities.caption_eligible:
            raise ValueError("当前语音档案不能用于语音字幕，请更换档案")
        if (
            settings.translation.voice_route.source_language == "auto"
            and not capabilities.source_language_auto
        ):
            # The ASR provider may bind its recognition language to the
            # selected engine/AppKey while the downstream text translator can
            # still auto-detect the recognized text. Warn in diagnostics, but
            # do not make the overlay's start control appear unresponsive.
            self._logger.info(
                "voice_asr_uses_fixed_language_with_auto_translation_source"
            )
        if profile_validation_state(profile) != "verified":
            raise ValueError("该语音档案尚未验证，请先执行实时连接验证并保存设置")
        if not capabilities.final_transcript:
            raise ValueError("当前语音服务不返回识别原文，无法进入文字翻译路由")

    def _clear_start_worker(self, worker: TaskWorker) -> None:
        if self._start_worker is worker:
            self._start_worker = None
