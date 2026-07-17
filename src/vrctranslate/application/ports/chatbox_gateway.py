from __future__ import annotations

from typing import Protocol

from vrctranslate.application.dto import OscSettings


class ChatboxGateway(Protocol):
    def send_input(self, text: str, settings: OscSettings) -> None: ...

    def send_typing(self, typing: bool, settings: OscSettings) -> None: ...

