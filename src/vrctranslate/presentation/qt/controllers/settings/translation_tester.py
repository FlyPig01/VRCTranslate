from __future__ import annotations

import logging
from uuid import uuid4

from PySide6.QtCore import QObject, QThreadPool

from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.translation import TranslationRequest, TranslationResult
from vrctranslate.presentation.qt.i18n import I18nManager
from vrctranslate.presentation.qt.pages.settings_page import SettingsPage
from vrctranslate.presentation.qt.workers.task_worker import TaskWorker


class TranslationProfileTester(QObject):
    """Runs the fixed-sentence provider test away from settings persistence."""

    def __init__(
        self,
        page: SettingsPage,
        translate_text: TranslateText,
        logger: logging.Logger,
        i18n: I18nManager | None,
        parent: QObject | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._translate_text = translate_text
        self._logger = logger
        self._i18n = i18n
        self._thread_pool = QThreadPool.globalInstance()
        self._testing_profile_id = ""

    def run(self) -> None:
        profile = self._page.selected_profile()
        self._testing_profile_id = profile.id
        request = TranslationRequest(
            uuid4().hex,
            "Please keep VRChat unchanged.",
            "en",
            "zh-CN",
            "self",
        )
        status = (
            self._i18n.tr("ctrl.settings.testing")
            if self._i18n
            else "正在使用固定短句测试…"
        )
        self._page.set_test_status(status)
        worker = TaskWorker(lambda: self._translate_text.execute(request, profile))
        worker.signals.succeeded.connect(self._succeeded)
        worker.signals.failed.connect(self._failed)
        self._thread_pool.start(worker)

    def _succeeded(self, value: object) -> None:
        if not isinstance(value, TranslationResult):
            return
        message = (
            self._success_message(value)
            if self._i18n
            else f"测试成功：{value.translated}"
        )
        self._page.set_test_status(message)

    def _success_message(self, value: TranslationResult) -> str:
        status = self._translate_text.glossary_status(self._testing_profile_id)
        key = {
            "compatible": "ctrl.settings.test_ok_glossary",
            "fallback": "ctrl.settings.test_ok_glossary_fallback",
            "prompt": "ctrl.settings.test_ok_glossary_prompt",
        }.get(status, "ctrl.settings.test_ok")
        return self._i18n.tr(key, text=value.translated) if self._i18n else value.translated

    def _failed(self, error: object) -> None:
        if isinstance(error, VrcTranslateError):
            self._page.set_test_status(error.user_message, True)
        else:
            message = (
                self._i18n.tr("ctrl.settings.test_fail")
                if self._i18n
                else "测试失败，请查看运行日志"
            )
            self._page.set_test_status(message, True)
        self._logger.warning(
            "translation_test_failed category=%s",
            getattr(error, "category", "unexpected"),
        )
