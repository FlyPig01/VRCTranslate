from dataclasses import replace

from vrctranslate.application.dto import AppSettings, OcrSettings, OscSettings
from vrctranslate.application.use_cases.manage_settings import ManageSettings
from vrctranslate.application.use_cases.prepare_chatbox_message import (
    PrepareChatboxMessage,
)
from vrctranslate.application.use_cases.process_ocr_frame import ProcessOcrFrame
from vrctranslate.application.use_cases.send_chatbox_message import ChatboxSendQueue
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.chatbox import MessageFormat
from vrctranslate.domain.errors import ChatboxSendFailed
from vrctranslate.domain.ocr import CapturedFrame, OcrText
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


class FakeTranslator:
    def translate(self, request, settings):
        return TranslationResult(
            request.request_id,
            request.text,
            f"translated:{request.text}",
            request.source_language,
            request.target_language,
            request.purpose,
        )


class FakeGateway:
    def __init__(self) -> None:
        self.messages: list[str] = []
        self.typing: list[bool] = []
        self.fail = False

    def send_input(self, text, settings):
        if self.fail:
            raise ChatboxSendFailed("failed")
        self.messages.append(text)

    def send_typing(self, typing, settings):
        self.typing.append(typing)


class FakeOcrEngine:
    def __init__(self) -> None:
        self.calls = 0

    def recognize(self, frame):
        self.calls += 1
        return [OcrText("hello", 0.9), OcrText("uncertain", 0.2)]


class MemorySettingsRepository:
    location = "memory://settings"

    def __init__(self) -> None:
        self.settings = AppSettings()

    def load(self):
        return self.settings

    def save(self, settings):
        self.settings = settings


def test_translate_use_case_has_no_provider_dependency() -> None:
    use_case = TranslateText(FakeTranslator())
    request = TranslationRequest("1", "hello", "en", "zh-CN")
    result = use_case.execute(request, AppSettings().translation)
    assert result.translated == "translated:hello"


def test_prepare_chatbox_message_returns_explicit_limit_state() -> None:
    use_case = PrepareChatboxMessage()
    prepared = use_case.execute(
        "hello", "你好", MessageFormat.ORIGINAL_THEN_TRANSLATION, 5
    )
    assert prepared.exceeds_limit
    assert use_case.truncate(prepared) == "hello"
    assert all(len(part) <= 5 for part in use_case.split(prepared))


def test_send_queue_is_independent_from_qt_and_udp() -> None:
    gateway = FakeGateway()
    queue = ChatboxSendQueue(gateway)
    settings = OscSettings(min_interval_seconds=1)
    queue.enqueue("one")
    queue.enqueue("two")
    assert queue.drain_once(settings, now=10).sent
    assert queue.drain_once(settings, now=10.5) is None
    assert queue.drain_once(settings, now=11).sent
    assert gateway.messages == ["one", "two"]


def test_typing_synchronization_is_always_enabled() -> None:
    gateway = FakeGateway()
    queue = ChatboxSendQueue(gateway)
    queue.set_typing(True, OscSettings())
    queue.set_typing(False, OscSettings())
    assert gateway.typing == [True, False]


def test_send_queue_maps_gateway_failure_to_result() -> None:
    gateway = FakeGateway()
    gateway.fail = True
    queue = ChatboxSendQueue(gateway)
    queue.enqueue("one")
    result = queue.drain_once(OscSettings(), now=10)
    assert result is not None and not result.sent
    assert result.error_message == "failed"


def test_process_ocr_frame_filters_changes_confidence_and_duplicates() -> None:
    engine = FakeOcrEngine()
    use_case = ProcessOcrFrame(engine)
    settings = OcrSettings(confidence=0.5, change_threshold=2)
    first = use_case.execute(CapturedFrame(object(), b"\x00\x00"), settings)
    unchanged = use_case.execute(CapturedFrame(object(), b"\x00\x00"), settings)
    changed_duplicate = use_case.execute(
        CapturedFrame(object(), b"\x0a\x0a"), settings
    )
    assert [item.text for item in first] == ["hello"]
    assert unchanged == []
    assert changed_duplicate == []
    assert engine.calls == 2


def test_continuous_ocr_keeps_static_region_quiet_when_another_region_changes() -> None:
    class SequencedEngine:
        def __init__(self) -> None:
            self.index = 0

        def recognize(self, _frame):
            frames = (
                [
                    OcrText("固定标题", 0.95, ((0, 0), (80, 0), (80, 20), (0, 20))),
                    OcrText("第一句", 0.95, ((0, 25), (80, 25), (80, 45), (0, 45))),
                ],
                [
                    OcrText("固定标题", 0.95, ((1, 0), (81, 0), (81, 20), (1, 20))),
                    OcrText("第二句", 0.95, ((0, 25), (80, 25), (80, 45), (0, 45))),
                ],
            )
            result = frames[min(self.index, 1)]
            self.index += 1
            return result

    use_case = ProcessOcrFrame(SequencedEngine())
    settings = OcrSettings(confidence=0.5, change_threshold=1, recognition_mode="continuous")

    first = use_case.execute(CapturedFrame(object(), b"\x00\x00"), settings)
    second = use_case.execute(CapturedFrame(object(), b"\x10\x10"), settings)

    assert [item.text for item in first] == ["固定标题第一句"]
    assert [item.text for item in second] == ["第二句"]


def test_manage_settings_uses_repository_port() -> None:
    repository = MemorySettingsRepository()
    service = ManageSettings(repository)
    assert service.current.osc.port == 9000
    updated = replace(service.current, osc=replace(service.current.osc, port=9100))
    service.save(updated)
    assert repository.settings.osc.port == 9100
