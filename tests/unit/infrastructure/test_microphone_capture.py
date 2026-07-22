from __future__ import annotations

from vrctranslate.infrastructure.audio.sounddevice_microphone import (
    SoundDeviceMicrophoneCapture,
)


class _Stream:
    def __init__(self, **kwargs) -> None:
        self.callback = kwargs["callback"]
        self.started = False
        self.aborted = False
        self.closed = False

    def start(self) -> None:
        self.started = True

    def abort(self) -> None:
        self.aborted = True

    def close(self) -> None:
        self.closed = True


class _DefaultDevice:
    device = (1, 2)


class _SoundDevice:
    default = _DefaultDevice()

    def __init__(self) -> None:
        self.stream: _Stream | None = None

    def query_devices(self, device=None, kind=None):
        if kind == "input":
            return {"default_samplerate": 16000}
        devices = [
            {
                "name": "Microsoft Sound Mapper - Input",
                "max_input_channels": 2,
                "hostapi": 0,
            },
            {
                "name": "Main microphone",
                "max_input_channels": 2,
                "hostapi": 0,
            },
            {
                "name": "Main microphone",
                "max_input_channels": 2,
                "hostapi": 1,
            },
            {"name": "Desk microphone", "max_input_channels": 1, "hostapi": 1},
            {"name": "Output only", "max_input_channels": 0, "hostapi": 1},
        ]
        return devices[int(device)] if device is not None else devices

    def query_hostapis(self):
        return [
            {"name": "MME", "default_input_device": 1},
            {"name": "Windows WASAPI", "default_input_device": 2},
        ]

    def RawInputStream(self, **kwargs):
        self.stream = _Stream(**kwargs)
        return self.stream


def test_sounddevice_microphone_lists_inputs_and_marks_default() -> None:
    module = _SoundDevice()
    capture = SoundDeviceMicrophoneCapture(module)

    devices = capture.list_devices()

    assert [
        (item.id, item.name, item.is_default, item.host_api) for item in devices
    ] == [
        ("2", "Main microphone", True, "Windows WASAPI"),
        ("3", "Desk microphone", False, "Windows WASAPI"),
    ]


def test_sounddevice_microphone_streams_memory_frames_and_releases_device() -> None:
    module = _SoundDevice()
    capture = SoundDeviceMicrophoneCapture(module)
    frames = []

    capture.start("2", frames.append)
    assert capture.running
    assert module.stream is not None
    module.stream.callback(b"\x01\x00" * 1600, 1600, None, None)

    assert len(frames) == 1
    assert frames[0].sample_rate == 16000
    assert frames[0].pcm16 == b"\x01\x00" * 1600

    capture.stop()
    assert capture.running is False
    assert module.stream.aborted
    assert module.stream.closed
