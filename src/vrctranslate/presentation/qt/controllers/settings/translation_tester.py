from __future__ import annotations

import logging
from io import BytesIO
from uuid import uuid4

from PySide6.QtCore import QObject, QThreadPool
from PIL import Image, ImageDraw

from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.application.use_cases.translate_visual_frame import TranslateVisualFrame
from vrctranslate.domain.errors import VrcTranslateError
from vrctranslate.domain.translation import TranslationRequest, TranslationResult
from vrctranslate.domain.visual_translation import (
    VisualTranslationRequest,
    VisualTranslationResult,
)
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
        translate_visual: TranslateVisualFrame | None = None,
    ) -> None:
        super().__init__(parent)
        self._page = page
        self._translate_text = translate_text
        self._logger = logger
        self._i18n = i18n
        self._translate_visual = translate_visual
        self._thread_pool = QThreadPool.globalInstance()
        self._testing_profile_id = ""

    def run(self) -> None:
        profile = self._page.selected_profile()
        self._testing_profile_id = profile.id
        status = (
            self._i18n.tr(
                "ctrl.settings.testing_visual"
                if profile.provider == "multimodal_openai"
                else "ctrl.settings.testing"
            )
            if self._i18n
            else "正在使用固定短句测试…"
        )
        self._page.set_test_status(status)
        if profile.provider == "multimodal_openai":
            if self._translate_visual is None:
                self._page.set_test_status(
                    self._i18n.tr("ctrl.settings.test_fail")
                    if self._i18n
                    else "多模态测试组件未初始化",
                    True,
                )
                return
            request = VisualTranslationRequest(
                uuid4().hex,
                self._test_image(),
                "image/png",
                "en",
                "zh-CN",
            )
            worker = TaskWorker(
                lambda: self._translate_visual.execute(request, profile)
            )
        else:
            request = TranslationRequest(
                uuid4().hex,
                "Please keep VRChat unchanged.",
                "en",
                "zh-CN",
                "self",
            )
            worker = TaskWorker(
                lambda: self._translate_text.execute(request, profile)
            )
        worker.signals.succeeded.connect(self._succeeded)
        worker.signals.failed.connect(self._failed)
        self._thread_pool.start(worker)

    def _succeeded(self, value: object) -> None:
        if isinstance(value, VisualTranslationResult):
            self._page.set_test_status(
                self._i18n.tr(
                    "ctrl.settings.test_ok",
                    text=value.translated or "OK",
                )
                if self._i18n
                else f"测试成功：{value.translated or 'OK'}"
            )
            return
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
        self._page.set_test_status(self._failure_message(error), True)
        self._logger.warning(
            "translation_test_failed category=%s",
            getattr(error, "category", "unexpected"),
        )

    def _failure_message(self, error: object) -> str:
        category = str(getattr(error, "category", "unexpected"))
        if isinstance(error, VrcTranslateError):
            reason = error.user_message.strip()
        else:
            reason = " ".join(str(error).split()).strip()
            if not reason:
                reason = type(error).__name__
        reason = reason[:300]
        suggestion_key = {
            "configuration": "ctrl.settings.test_help_configuration",
            "authentication": "ctrl.settings.test_help_authentication",
            "quota": "ctrl.settings.test_help_quota",
            "network": "ctrl.settings.test_help_network",
            "service": "ctrl.settings.test_help_service",
            "response": "ctrl.settings.test_help_response",
        }.get(category, "ctrl.settings.test_help_unexpected")
        if self._i18n is not None:
            return self._i18n.tr(
                "ctrl.settings.test_fail_detail",
                reason=reason,
                suggestion=self._i18n.tr(suggestion_key),
            )
        return f"测试失败：{reason}\n处理建议：请检查配置和网络后重试；详细类别已写入日志。"

    @staticmethod
    def _test_image() -> bytes:
        image = Image.new("RGB", (360, 100), "white")
        ImageDraw.Draw(image).text((18, 38), "Hello VRChat", fill="black")
        output = BytesIO()
        image.save(output, format="PNG")
        return output.getvalue()
