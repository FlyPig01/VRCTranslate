import socket

from pythonosc.osc_packet import OscPacket

from vrctranslate.application.dto import OscSettings
from vrctranslate.infrastructure.osc.pythonosc_gateway import PythonOscGateway


def test_gateway_sends_chatbox_packet() -> None:
    receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    receiver.bind(("127.0.0.1", 0))
    receiver.settimeout(2)
    settings = OscSettings(port=receiver.getsockname()[1], play_sound=True)
    try:
        PythonOscGateway().send_input("hello", settings)
        payload, _address = receiver.recvfrom(4096)
        message = OscPacket(payload).messages[0].message
        assert message.address == "/chatbox/input"
        assert message.params == ["hello", True, True]
    finally:
        receiver.close()

