from __future__ import annotations

from pythonosc.udp_client import SimpleUDPClient

from vrctranslate.application.dto import OscSettings
from vrctranslate.domain.errors import ChatboxSendFailed


class PythonOscGateway:
    def __init__(self) -> None:
        self._target: tuple[str, int] | None = None
        self._client: SimpleUDPClient | None = None

    def send_input(self, text: str, settings: OscSettings) -> None:
        try:
            self._get_client(settings).send_message(
                "/chatbox/input",
                [text, True, settings.play_sound],
            )
        except (OSError, ValueError) as exc:
            raise ChatboxSendFailed(f"OSC 本地发送失败：{exc}") from exc

    def send_typing(self, typing: bool, settings: OscSettings) -> None:
        try:
            self._get_client(settings).send_message("/chatbox/typing", bool(typing))
        except (OSError, ValueError) as exc:
            raise ChatboxSendFailed(f"OSC typing 状态发送失败：{exc}") from exc

    def _get_client(self, settings: OscSettings) -> SimpleUDPClient:
        target = (settings.host, settings.port)
        if self._client is None or self._target != target:
            self._client = SimpleUDPClient(*target)
            self._target = target
        return self._client

