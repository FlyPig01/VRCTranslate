from __future__ import annotations

from collections import deque
from time import monotonic
from uuid import uuid4

from vrctranslate.application.dto import OscSettings
from vrctranslate.application.ports.chatbox_gateway import ChatboxGateway
from vrctranslate.domain.chatbox import ChatboxDrainResult, ChatboxQueueItem
from vrctranslate.domain.errors import ChatboxSendFailed


class ChatboxSendQueue:
    def __init__(self, gateway: ChatboxGateway) -> None:
        self._gateway = gateway
        self._items: deque[ChatboxQueueItem] = deque()
        self._last_sent_at = 0.0

    @property
    def count(self) -> int:
        return len(self._items)

    def enqueue(self, text: str) -> ChatboxQueueItem:
        item = ChatboxQueueItem(uuid4().hex, text)
        self._items.append(item)
        return item

    def clear(self) -> None:
        self._items.clear()

    def drain_once(
        self,
        settings: OscSettings,
        now: float | None = None,
    ) -> ChatboxDrainResult | None:
        if not self._items:
            return None
        timestamp = monotonic() if now is None else now
        if timestamp - self._last_sent_at < settings.min_interval_seconds:
            return None
        item = self._items.popleft()
        try:
            self._gateway.send_input(item.text, settings)
        except ChatboxSendFailed as exc:
            return ChatboxDrainResult(item.item_id, False, exc.user_message)
        self._last_sent_at = timestamp
        return ChatboxDrainResult(item.item_id, True)

    def set_typing(self, typing: bool, settings: OscSettings) -> None:
        # Typing synchronization is an invariant of the quick-input workflow.
        # There is intentionally no user-facing switch in configuration v2.
        self._gateway.send_typing(typing, settings)

    def shutdown(self, settings: OscSettings) -> None:
        try:
            self.set_typing(False, settings)
        except ChatboxSendFailed:
            pass
        self.clear()
