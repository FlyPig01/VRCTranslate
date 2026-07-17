from __future__ import annotations

from typing import Generic, TypeVar


OutcomeT = TypeVar("OutcomeT")


class OrderedCompletionBuffer(Generic[OutcomeT]):
    """Reorders concurrent results without knowing how they were produced."""

    def __init__(self) -> None:
        self._next_sequence = 0
        self._completed: dict[int, OutcomeT | None] = {}

    def reset(self) -> None:
        self._next_sequence = 0
        self._completed.clear()

    def add(self, sequence: int, outcome: OutcomeT | None) -> list[OutcomeT]:
        ready: list[OutcomeT] = []
        self._completed[sequence] = outcome
        while self._next_sequence in self._completed:
            current = self._completed.pop(self._next_sequence)
            self._next_sequence += 1
            if current is not None:
                ready.append(current)
        return ready
