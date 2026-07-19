from __future__ import annotations

import ctypes
import threading

from vrctranslate.infrastructure.audio import windows_process_loopback as loopback


class _FakeCompletion:
    def __init__(self) -> None:
        self.completed = threading.Event()
        self.completed.set()
        self.activation_result = 0
        self.audio_client = ctypes.c_void_p(0x1111)
        self.object = loopback._CompletionObject()


class _FakeActivate:
    argtypes = None
    restype = None

    def __call__(self, _device, _iid, _variant, _completion, output) -> int:
        ctypes.cast(output, ctypes.POINTER(ctypes.c_void_p))[0] = ctypes.c_void_p(
            0x2222
        )
        return 0


class _FakeMmdevapi:
    ActivateAudioInterfaceAsync = _FakeActivate()


def test_async_activation_operation_is_released_as_soon_as_result_is_ready(
    monkeypatch,
) -> None:
    released: list[int] = []
    monkeypatch.setattr(loopback, "_ActivationCompletion", _FakeCompletion)
    monkeypatch.setattr(
        loopback,
        "_release_interface",
        lambda value: released.append(int(value.value)) if value and value.value else None,
    )

    audio_client = loopback.WindowsProcessAudioCapture._activate(
        _FakeMmdevapi(),
        123,
        True,
    )

    assert audio_client.value == 0x1111
    assert released == [0x2222]
