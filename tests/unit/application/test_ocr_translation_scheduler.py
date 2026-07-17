from __future__ import annotations

from threading import Event
from time import monotonic, sleep

from vrctranslate.application.dto import TranslationProfile, TranslationSettings
from vrctranslate.application.use_cases.ocr_translation_scheduler import OcrTranslationScheduler
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


class ControlledTranslator:
    def __init__(self) -> None:
        self.calls = 0
        self.gates: dict[str, Event] = {}

    def translate(self, request, profile):
        self.calls += 1
        gate = self.gates.get(request.request_id)
        if gate is not None:
            gate.wait(2)
        return TranslationResult(
            request.request_id,
            request.text,
            f"translated:{request.text}",
            request.source_language,
            request.target_language,
            request.purpose,
        )


class BatchTranslator(ControlledTranslator):
    def __init__(self) -> None:
        super().__init__()
        self.batch_calls = 0

    def translate_batch(self, requests, profile):
        self.batch_calls += 1
        return [self.translate(request, profile) for request in requests]


def _settings(limit: int = 8) -> TranslationSettings:
    settings = TranslationSettings(profiles=[TranslationProfile()])
    settings.ocr_route.queue_limit = limit
    settings.ocr_route.task_ttl_seconds = 2
    return settings


def _request(request_id: str, text: str = "hello") -> TranslationRequest:
    return TranslationRequest(request_id, text, "en", "zh-CN", "ocr")


def _wait_until(predicate, timeout: float = 2) -> None:
    deadline = monotonic() + timeout
    while monotonic() < deadline:
        if predicate():
            return
        sleep(0.01)
    raise AssertionError("timed out")


def test_scheduler_never_exceeds_bounded_capacity() -> None:
    translator = ControlledTranslator()
    translator.gates = {"1": Event(), "2": Event()}
    scheduler = OcrTranslationScheduler(TranslateText(translator), lambda _: None)
    scheduler.start(_settings(limit=2))
    assert scheduler.submit(_request("1"))
    assert scheduler.submit(_request("2"))
    assert not scheduler.submit(_request("3"))
    translator.gates["1"].set()
    translator.gates["2"].set()
    scheduler.shutdown()


def test_scheduler_emits_in_recognition_order_and_uses_memory_cache() -> None:
    translator = ControlledTranslator()
    translator.gates["1"] = Event()
    outcomes = []
    scheduler = OcrTranslationScheduler(TranslateText(translator), outcomes.append)
    scheduler.start(_settings())
    assert scheduler.submit(_request("1", "first"))
    assert scheduler.submit(_request("2", "second"))
    sleep(0.05)
    assert outcomes == []
    translator.gates["1"].set()
    _wait_until(lambda: len(outcomes) == 2)
    assert [item.request_id for item in outcomes] == ["1", "2"]
    assert scheduler.submit(_request("3", "second"))
    _wait_until(lambda: len(outcomes) == 3)
    assert outcomes[-1].cached
    assert translator.calls == 2
    scheduler.shutdown()


def test_scheduler_ignores_results_from_stopped_session() -> None:
    translator = ControlledTranslator()
    translator.gates["old"] = Event()
    outcomes = []
    scheduler = OcrTranslationScheduler(TranslateText(translator), outcomes.append)
    scheduler.start(_settings())
    scheduler.submit(_request("old"))
    scheduler.stop()
    translator.gates["old"].set()
    sleep(0.05)
    assert outcomes == []


def test_fast_provider_batches_lines_from_the_same_frame() -> None:
    translator = BatchTranslator()
    outcomes = []
    scheduler = OcrTranslationScheduler(TranslateText(translator), outcomes.append)
    scheduler.start(_settings())
    accepted = scheduler.submit_many(
        [_request("1", "first"), _request("2", "second")]
    )
    assert accepted == {"1", "2"}
    _wait_until(lambda: len(outcomes) == 2)
    assert translator.batch_calls == 1
    assert [outcome.request_id for outcome in outcomes] == ["1", "2"]
    scheduler.shutdown()
