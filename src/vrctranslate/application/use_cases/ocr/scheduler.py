from __future__ import annotations

from concurrent.futures import Future, ThreadPoolExecutor
from copy import deepcopy
from dataclasses import dataclass
from threading import Lock
from time import monotonic
from typing import Callable

from vrctranslate.application.dto import TranslationProfile, TranslationSettings
from vrctranslate.application.use_cases.ocr.session_cache import (
    CacheKey,
    SessionTranslationCache,
)
from vrctranslate.application.use_cases.ocr.completion_buffer import (
    OrderedCompletionBuffer,
)
from vrctranslate.application.use_cases.ocr.execution_policy import (
    OcrExecutionPolicy,
)
from vrctranslate.application.use_cases.ocr.task_queue import BoundedTaskQueue
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.text_rules import normalize_text
from vrctranslate.domain.translation import TranslationRequest, TranslationResult


@dataclass(frozen=True, slots=True)
class OcrTranslationOutcome:
    request_id: str
    result: TranslationResult | None = None
    error: Exception | None = None
    cached: bool = False


class OcrTranslationScheduler:
    """Coordinate one bounded, expiring and ordered OCR translation session."""

    def __init__(
        self,
        translate_text: TranslateText,
        outcome_callback: Callable[[OcrTranslationOutcome], None],
    ) -> None:
        self._translate_text = translate_text
        self._callback = outcome_callback
        self._lock = Lock()
        self._generation = 0
        self._sequence = 0
        self._profile: TranslationProfile | None = None
        self._policy: OcrExecutionPolicy | None = None
        self._ttl = 4.0
        self._executor: ThreadPoolExecutor | None = None
        self._queue: BoundedTaskQueue | None = None
        self._futures: set[Future[object]] = set()
        self._completion_buffer = OrderedCompletionBuffer[OcrTranslationOutcome]()
        self._cache = SessionTranslationCache()

    @property
    def pending_count(self) -> int:
        with self._lock:
            return len(self._futures)

    def start(self, settings: TranslationSettings) -> int:
        self.stop()
        settings.ensure_routes()
        route = settings.ocr_route
        profile = deepcopy(settings.profile(route.profile_id))
        profile.timeout_seconds = min(profile.timeout_seconds, route.timeout_seconds)
        policy = OcrExecutionPolicy.create(profile, route)
        with self._lock:
            self._generation += 1
            self._sequence = 0
            self._profile = profile
            self._policy = policy
            self._ttl = route.task_ttl_seconds
            self._queue = BoundedTaskQueue(policy.queue_capacity)
            self._executor = ThreadPoolExecutor(
                max_workers=policy.max_workers,
                thread_name_prefix="ocr-translation",
            )
            self._completion_buffer.reset()
            self._cache.clear()
            return self._generation

    def submit(self, request: TranslationRequest) -> bool:
        return request.request_id in self.submit_many([request])

    def submit_many(self, requests: list[TranslationRequest]) -> set[str]:
        with self._lock:
            profile = self._profile
            policy = self._policy
        if (
            len(requests) >= 2
            and profile is not None
            and policy is not None
            and policy.batch_enabled
        ):
            return self._submit_batch(requests)
        return {
            request.request_id for request in requests if self._submit_single(request)
        }

    def _session(self):
        with self._lock:
            return (
                self._executor,
                self._queue,
                self._profile,
                self._generation,
            )

    def _reserve(self, generation: int, executor: ThreadPoolExecutor) -> int | None:
        with self._lock:
            if generation != self._generation or executor is not self._executor:
                return None
            sequence = self._sequence
            self._sequence += 1
            return sequence

    def _submit_single(self, request: TranslationRequest) -> bool:
        executor, queue, profile, generation = self._session()
        if executor is None or queue is None or profile is None or not queue.try_acquire():
            return False
        sequence = self._reserve(generation, executor)
        if sequence is None:
            queue.release()
            return False
        created_at = monotonic()
        key = self._cache.key(request, profile)
        cached = self._cache.get(key)
        if cached is not None:
            queue.release()
            result = TranslationResult(
                request.request_id,
                normalize_text(request.text),
                cached.translated,
                request.source_language,
                request.target_language,
                request.purpose,
            )
            self._complete(
                generation,
                sequence,
                OcrTranslationOutcome(request.request_id, result=result, cached=True),
            )
            return True
        future = executor.submit(
            self._execute_one, generation, created_at, request, deepcopy(profile)
        )
        with self._lock:
            self._futures.add(future)
        future.add_done_callback(
            lambda done: self._single_done(
                done, generation, sequence, request, key, queue, created_at
            )
        )
        return True

    def _submit_batch(self, requests: list[TranslationRequest]) -> set[str]:
        executor, queue, profile, generation = self._session()
        if executor is None or queue is None or profile is None:
            return set()
        accepted: set[str] = set()
        misses: list[tuple[int, TranslationRequest, CacheKey]] = []
        created_at = monotonic()
        for request in requests:
            if not queue.try_acquire():
                break
            sequence = self._reserve(generation, executor)
            if sequence is None:
                queue.release()
                break
            accepted.add(request.request_id)
            key = self._cache.key(request, profile)
            cached = self._cache.get(key)
            if cached is None:
                misses.append((sequence, request, key))
                continue
            queue.release()
            result = TranslationResult(
                request.request_id,
                normalize_text(request.text),
                cached.translated,
                request.source_language,
                request.target_language,
                request.purpose,
            )
            self._complete(
                generation,
                sequence,
                OcrTranslationOutcome(request.request_id, result=result, cached=True),
            )
        if not misses:
            return accepted
        future = executor.submit(
            self._execute_batch,
            generation,
            created_at,
            [entry[1] for entry in misses],
            deepcopy(profile),
        )
        with self._lock:
            self._futures.add(future)
        future.add_done_callback(
            lambda done: self._batch_done(
                done, generation, misses, queue, created_at
            )
        )
        return accepted

    def _execute_one(
        self,
        generation: int,
        created_at: float,
        request: TranslationRequest,
        profile: TranslationProfile,
    ) -> TranslationResult | None:
        if self._expired(generation, created_at):
            return None
        return self._translate_text.execute(request, profile)

    def _execute_batch(
        self,
        generation: int,
        created_at: float,
        requests: list[TranslationRequest],
        profile: TranslationProfile,
    ) -> list[TranslationResult] | None:
        if self._expired(generation, created_at):
            return None
        return self._translate_text.execute_batch(requests, profile)

    def _expired(self, generation: int, created_at: float) -> bool:
        with self._lock:
            return generation != self._generation or monotonic() - created_at > self._ttl

    def _single_done(
        self,
        future: Future[TranslationResult | None],
        generation: int,
        sequence: int,
        request: TranslationRequest,
        key: CacheKey,
        queue: BoundedTaskQueue,
        created_at: float,
    ) -> None:
        queue.release()
        result: TranslationResult | None = None
        try:
            result = future.result()
            outcome = (
                OcrTranslationOutcome(request.request_id, result=result)
                if result is not None
                else None
            )
        except Exception as exc:
            outcome = OcrTranslationOutcome(request.request_id, error=exc)
        with self._lock:
            self._futures.discard(future)
            if generation != self._generation:
                return
            fresh = monotonic() - created_at <= self._ttl
            if result is not None and fresh:
                self._cache.put(key, result)
            elif result is not None:
                outcome = None
        self._complete(generation, sequence, outcome)

    def _batch_done(
        self,
        future: Future[object],
        generation: int,
        entries: list[tuple[int, TranslationRequest, CacheKey]],
        queue: BoundedTaskQueue,
        created_at: float,
    ) -> None:
        for _ in entries:
            queue.release()
        error: Exception | None = None
        results: list[TranslationResult] | None = None
        try:
            value = future.result()
            if value is not None:
                results = list(value)  # type: ignore[arg-type]
                if len(results) != len(entries):
                    raise RuntimeError("batch translation count mismatch")
        except Exception as exc:
            error = exc
        completions: list[tuple[int, OcrTranslationOutcome | None]] = []
        with self._lock:
            self._futures.discard(future)
            if generation != self._generation:
                return
            expired = monotonic() - created_at > self._ttl
            for index, (sequence, request, key) in enumerate(entries):
                if expired or (results is None and error is None):
                    outcome = None
                elif error is not None:
                    outcome = OcrTranslationOutcome(request.request_id, error=error)
                else:
                    result = results[index]
                    self._cache.put(key, result)
                    outcome = OcrTranslationOutcome(request.request_id, result=result)
                completions.append((sequence, outcome))
        for sequence, outcome in completions:
            self._complete(generation, sequence, outcome)

    def _complete(
        self,
        generation: int,
        sequence: int,
        outcome: OcrTranslationOutcome | None,
    ) -> None:
        ready: list[OcrTranslationOutcome] = []
        with self._lock:
            if generation != self._generation:
                return
            ready = self._completion_buffer.add(sequence, outcome)
        for current in ready:
            self._callback(current)

    def stop(self) -> None:
        with self._lock:
            self._generation += 1
            executor = self._executor
            futures = tuple(self._futures)
            self._executor = None
            self._queue = None
            self._profile = None
            self._policy = None
            self._futures.clear()
            self._completion_buffer.reset()
            self._cache.clear()
        for future in futures:
            future.cancel()
        if executor is not None:
            executor.shutdown(wait=False, cancel_futures=True)

    def shutdown(self) -> None:
        self.stop()
