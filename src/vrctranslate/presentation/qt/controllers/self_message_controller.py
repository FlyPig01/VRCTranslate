from __future__ import annotations

import logging
from collections import deque
from copy import deepcopy
from dataclasses import dataclass
from uuid import uuid4

from PySide6.QtCore import QObject, QThreadPool, QTimer, Signal

from vrctranslate.application.dto import TranslationProfile, TranslationRouteSettings
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.prepare_chatbox_message import PrepareChatboxMessage
from vrctranslate.application.use_cases.send_chatbox_message import ChatboxSendQueue
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.chatbox import MessageFormat
from vrctranslate.domain.errors import ChatboxSendFailed, VrcTranslateError
from vrctranslate.domain.translation import TranslationRequest, TranslationResult
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.self_message_page import SelfMessagePage
from vrctranslate.presentation.qt.windows.quick_input_window import QuickInputWindow
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker


@dataclass(slots=True)
class _PendingMessage:
    request: TranslationRequest
    original: str
    profile: TranslationProfile
    route: TranslationRouteSettings


class SelfMessageController(QObject):
    status_bar_message = Signal(str, int)

    def __init__(
        self,
        page: SelfMessagePage,
        quick_window: QuickInputWindow,
        translate_text: TranslateText,
        prepare_message: PrepareChatboxMessage,
        send_queue: ChatboxSendQueue,
        settings: ManageSettings,
        logger: logging.Logger,
        i18n: I18nManager | None = None,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._window = quick_window
        self._translate_text = translate_text
        self._prepare_message = prepare_message
        self._send_queue = send_queue
        self._settings = settings
        self._logger = logger
        self._i18n = i18n
        self._thread_pool = QThreadPool.globalInstance()
        self._pending: deque[_PendingMessage] = deque()
        self._active: _PendingMessage | None = None
        self._typing_active = False

        self._queue_timer = QTimer(self)
        self._queue_timer.setInterval(100)
        self._queue_timer.timeout.connect(self._drain_queue)
        self._queue_timer.start()
        self._typing_timer = QTimer(self)
        self._typing_timer.setSingleShot(True)
        self._typing_timer.setInterval(1500)
        self._typing_timer.timeout.connect(lambda: self._set_typing(False))
        self._geometry_timer = QTimer(self)
        self._geometry_timer.setSingleShot(True)
        self._geometry_timer.setInterval(400)
        self._geometry_timer.timeout.connect(self._save_geometry)
        self._input_settings_timer = QTimer(self)
        self._input_settings_timer.setSingleShot(True)
        self._input_settings_timer.setInterval(350)
        self._input_settings_timer.timeout.connect(self._save_input_settings)

        page.input_settings_changed.connect(self._preview_input_settings)
        quick_window.submitted.connect(self._submitted)
        quick_window.text_activity.connect(self._text_activity)
        quick_window.hidden_by_user.connect(lambda: self._set_typing(False))
        quick_window.geometry_changed.connect(lambda *_: self._geometry_timer.start())
        self.apply_settings(settings.current)

    def _tr(self, key: str, **kwargs: object) -> str:
        if self._i18n is not None:
            return self._i18n.tr(key, **kwargs)
        return key

    @property
    def target_language(self) -> str:
        return self._settings.current.translation.self_route.target_language

    def apply_settings(self, settings: object) -> None:
        if not hasattr(settings, "translation") or not hasattr(settings, "ui"):
            return
        app_settings = settings
        app_settings.translation.ensure_routes()
        profile = app_settings.translation.profile_for_purpose("self")
        self._page.set_profile(profile.name)
        self._window.apply_settings(app_settings.ui)
        self._page.load_ui_settings(app_settings.ui)

    def _preview_input_settings(self, topmost: bool, width: int) -> None:
        ui = self._settings.current.ui
        ui.input_topmost = topmost
        ui.input_width = width
        self._window.apply_settings(ui)
        self._input_settings_timer.start()

    def _save_input_settings(self) -> None:
        try:
            self._settings.save(self._settings.current)
        except OSError as exc:
            self.status_bar_message.emit(
                self._tr("ctrl.settings.save_failed", error=str(exc)), 6000
            )

    def _submitted(self, text: str) -> None:
        original = self._window.take_text()
        if not original:
            return
        self._set_typing(False)
        translation = self._settings.current.translation
        translation.ensure_routes()
        route = deepcopy(translation.self_route)
        profile = deepcopy(translation.profile(route.profile_id))
        profile.timeout_seconds = min(profile.timeout_seconds, route.timeout_seconds)
        request = TranslationRequest(
            uuid4().hex,
            original,
            route.source_language,
            route.target_language,
            "self",
        )
        self._pending.append(_PendingMessage(request, original, profile, route))
        self._set_status(self._tr("ctrl.self.pending", count=len(self._pending)), "busy")
        self._start_next()

    def _start_next(self) -> None:
        if self._active is not None or not self._pending:
            return
        self._active = self._pending.popleft()
        current = self._active
        worker = TaskWorker(
            lambda: self._translate_text.execute(current.request, current.profile)
        )
        worker.signals.succeeded.connect(self._translation_succeeded)
        worker.signals.failed.connect(self._translation_failed)
        worker.signals.finished.connect(self._translation_finished)
        self._set_status(self._tr("ctrl.self.translating"), "busy")
        self._thread_pool.start(worker)

    def _translation_succeeded(self, value: object) -> None:
        if not isinstance(value, TranslationResult) or self._active is None:
            return
        current = self._active
        if value.request_id != current.request.request_id:
            return
        try:
            message_format = MessageFormat(current.route.message_format)
        except ValueError:
            message_format = MessageFormat.TRANSLATION_ONLY
        osc = self._settings.current.osc
        prepared = self._prepare_message.execute(
            current.original,
            value.translated,
            message_format,
            osc.chatbox_max_units,
        )
        messages = [prepared.text]
        if prepared.exceeds_limit:
            if current.route.overflow_policy == "split":
                messages = self._prepare_message.split(prepared)
            elif current.route.overflow_policy == "truncate":
                messages = [self._prepare_message.truncate(prepared)]
            else:
                self._window.restore_text(current.original)
                self._set_status(self._tr("ctrl.self.overflow"), "error")
                return
        for message in messages:
            self._send_queue.enqueue(message)
        self._page.set_last_translation(current.original, value.translated)
        self._set_status(self._tr("ctrl.self.queued", count=len(messages)), "success")
        self.status_bar_message.emit(
            self._tr("ctrl.self.status_queue", count=self._send_queue.count), 3000
        )

    def _translation_failed(self, error: object) -> None:
        if self._active is None:
            return
        category = getattr(error, "category", "unexpected")
        self._logger.warning("self_translation_failed category=%s", category)
        self._window.restore_text(self._active.original)
        message = (
            error.user_message
            if isinstance(error, VrcTranslateError)
            else self._tr("ctrl.self.failed")
        )
        self._set_status(message, "error")

    def _translation_finished(self) -> None:
        self._active = None
        self._start_next()

    def _text_activity(self, text: str) -> None:
        if text.strip():
            self._set_typing(True)
            self._typing_timer.start()
            if self._active is None:
                self._set_status(self._tr("ctrl.self.typing"), "idle")
        else:
            self._typing_timer.stop()
            self._set_typing(False)

    def _set_typing(self, typing: bool) -> None:
        if typing == self._typing_active:
            return
        try:
            self._send_queue.set_typing(typing, self._settings.current.osc)
            self._typing_active = typing
        except ChatboxSendFailed:
            self._logger.warning("osc_typing_failed")

    def _drain_queue(self) -> None:
        result = self._send_queue.drain_once(self._settings.current.osc)
        if result is None:
            return
        if result.sent:
            self._set_status(self._tr("ctrl.self.sent"), "success")
            self.status_bar_message.emit(self._tr("ctrl.self.status_sent"), 4000)
            self._logger.info("osc_item_sent")
        else:
            self._set_status(result.error_message, "error")
            self._logger.warning("osc_send_failed")

    def _set_status(self, message: str, state: str) -> None:
        self._window.set_state(state, message)
        self._page.set_status(message)

    def _save_geometry(self) -> None:
        ui = self._settings.current.ui
        ui.input_x = self._window.x()
        ui.input_y = self._window.y()
        self._settings.save(self._settings.current)

    def shutdown(self) -> None:
        self._queue_timer.stop()
        self._typing_timer.stop()
        self._geometry_timer.stop()
        if self._input_settings_timer.isActive():
            self._input_settings_timer.stop()
            self._save_input_settings()
        self._set_typing(False)
        self._send_queue.shutdown(self._settings.current.osc)
        self._window.close_permanently()
