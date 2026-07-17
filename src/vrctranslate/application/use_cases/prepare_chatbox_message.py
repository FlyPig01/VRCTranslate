from __future__ import annotations

from vrctranslate.domain.chatbox import MessageFormat, PreparedChatboxMessage
from vrctranslate.domain.text_rules import (
    format_chatbox_message,
    split_utf16,
    truncate_utf16,
    utf16_units,
)


class PrepareChatboxMessage:
    def execute(
        self,
        original: str,
        translated: str,
        message_format: MessageFormat,
        maximum_units: int,
    ) -> PreparedChatboxMessage:
        text = format_chatbox_message(original, translated, message_format)
        return PreparedChatboxMessage(text, utf16_units(text), maximum_units)

    @staticmethod
    def truncate(message: PreparedChatboxMessage) -> str:
        return truncate_utf16(message.text, message.maximum_units)

    @staticmethod
    def split(message: PreparedChatboxMessage) -> list[str]:
        return split_utf16(message.text, message.maximum_units)

