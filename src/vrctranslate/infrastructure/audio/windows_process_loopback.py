from __future__ import annotations

import ctypes
import sys
import threading
import time
import uuid
from collections.abc import Callable
from ctypes import wintypes

import numpy as np

from vrctranslate.domain.speech import AudioFrame, ProcessAudioCaptureError


HRESULT = ctypes.c_long
REFERENCE_TIME = ctypes.c_longlong
WINFUNCTYPE = getattr(ctypes, "WINFUNCTYPE", ctypes.CFUNCTYPE)

S_OK = 0
E_NOINTERFACE = -2147467262
COINIT_MULTITHREADED = 0
CLSCTX_ALL = 23
VT_BLOB = 65
AUDCLNT_SHAREMODE_SHARED = 0
AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000
AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000
AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000
AUDCLNT_BUFFERFLAGS_SILENT = 0x2
PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0
PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1
SYNCHRONIZE = 0x00100000
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
WAIT_OBJECT_0 = 0
VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback"


class _GUID(ctypes.Structure):
    _fields_ = (
        ("Data1", wintypes.DWORD),
        ("Data2", wintypes.WORD),
        ("Data3", wintypes.WORD),
        ("Data4", ctypes.c_ubyte * 8),
    )

    @classmethod
    def from_text(cls, value: str) -> "_GUID":
        raw = uuid.UUID(value).bytes_le
        return cls.from_buffer_copy(raw)


IID_IAUDIO_CLIENT = _GUID.from_text("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")
IID_IAUDIO_CAPTURE_CLIENT = _GUID.from_text(
    "C8ADBD64-E71E-48a0-A4DE-185C395CD317"
)
IID_COMPLETION_HANDLER = _GUID.from_text(
    "41D949AB-9862-444A-80F6-C261334DA5EB"
)
IID_IUNKNOWN = _GUID.from_text("00000000-0000-0000-C000-000000000046")
IID_IAGILE_OBJECT = _GUID.from_text("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")


class _AudioClientProcessLoopbackParams(ctypes.Structure):
    _fields_ = (
        ("TargetProcessId", wintypes.DWORD),
        ("ProcessLoopbackMode", ctypes.c_int),
    )


class _ActivationUnion(ctypes.Union):
    _fields_ = (("ProcessLoopbackParams", _AudioClientProcessLoopbackParams),)


class _AudioClientActivationParams(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = (("ActivationType", ctypes.c_int), ("u", _ActivationUnion))


class _Blob(ctypes.Structure):
    _fields_ = (("cbSize", wintypes.ULONG), ("pBlobData", ctypes.c_void_p))


class _PropVariantUnion(ctypes.Union):
    _fields_ = (("blob", _Blob), ("pointer", ctypes.c_void_p))


class _PropVariant(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = (
        ("vt", wintypes.USHORT),
        ("wReserved1", wintypes.USHORT),
        ("wReserved2", wintypes.USHORT),
        ("wReserved3", wintypes.USHORT),
        ("u", _PropVariantUnion),
    )


class _WaveFormatEx(ctypes.Structure):
    _pack_ = 1
    _fields_ = (
        ("wFormatTag", wintypes.WORD),
        ("nChannels", wintypes.WORD),
        ("nSamplesPerSec", wintypes.DWORD),
        ("nAvgBytesPerSec", wintypes.DWORD),
        ("nBlockAlign", wintypes.WORD),
        ("wBitsPerSample", wintypes.WORD),
        ("cbSize", wintypes.WORD),
    )


_QueryInterface = WINFUNCTYPE(
    HRESULT, ctypes.c_void_p, ctypes.POINTER(_GUID), ctypes.POINTER(ctypes.c_void_p)
)
_AddRef = WINFUNCTYPE(wintypes.ULONG, ctypes.c_void_p)
_Release = WINFUNCTYPE(wintypes.ULONG, ctypes.c_void_p)
_ActivateCompleted = WINFUNCTYPE(HRESULT, ctypes.c_void_p, ctypes.c_void_p)


class _CompletionVTable(ctypes.Structure):
    _fields_ = (
        ("QueryInterface", _QueryInterface),
        ("AddRef", _AddRef),
        ("Release", _Release),
        ("ActivateCompleted", _ActivateCompleted),
    )


class _CompletionObject(ctypes.Structure):
    _fields_ = (("lpVtbl", ctypes.POINTER(_CompletionVTable)),)


def _guid_bytes(value: _GUID) -> bytes:
    return ctypes.string_at(ctypes.byref(value), ctypes.sizeof(value))


def _failed(result: int) -> bool:
    return int(result) < 0


def _hex_hresult(result: int) -> str:
    return f"0x{ctypes.c_uint32(result).value:08X}"


def _com_method(
    interface: ctypes.c_void_p,
    index: int,
    restype: type[ctypes._SimpleCData],  # type: ignore[attr-defined]
    *argtypes: object,
):
    table = ctypes.cast(
        interface, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))
    ).contents
    address = table[index]
    return WINFUNCTYPE(restype, ctypes.c_void_p, *argtypes)(address)


def _release_interface(interface: ctypes.c_void_p | None) -> None:
    if interface and interface.value:
        _com_method(interface, 2, wintypes.ULONG)(interface)


class _ActivationCompletion:
    def __init__(self) -> None:
        self.completed = threading.Event()
        self.activation_result = E_NOINTERFACE
        self.audio_client = ctypes.c_void_p()
        self._references = 1
        self._lock = threading.Lock()
        self._query_interface = _QueryInterface(self._on_query_interface)
        self._add_ref = _AddRef(self._on_add_ref)
        self._release = _Release(self._on_release)
        self._activate_completed = _ActivateCompleted(self._on_activate_completed)
        self._vtable = _CompletionVTable(
            self._query_interface,
            self._add_ref,
            self._release,
            self._activate_completed,
        )
        self.object = _CompletionObject(ctypes.pointer(self._vtable))

    def _on_query_interface(
        self,
        this: ctypes.c_void_p,
        requested: ctypes.POINTER(_GUID),
        output: ctypes.POINTER(ctypes.c_void_p),
    ) -> int:
        supported = {
            _guid_bytes(IID_IUNKNOWN),
            _guid_bytes(IID_COMPLETION_HANDLER),
            _guid_bytes(IID_IAGILE_OBJECT),
        }
        if requested and _guid_bytes(requested.contents) in supported:
            output[0] = this
            self._on_add_ref(this)
            return S_OK
        output[0] = None
        return E_NOINTERFACE

    def _on_add_ref(self, _this: ctypes.c_void_p) -> int:
        with self._lock:
            self._references += 1
            return self._references

    def _on_release(self, _this: ctypes.c_void_p) -> int:
        with self._lock:
            self._references = max(0, self._references - 1)
            return self._references

    def _on_activate_completed(
        self, _this: ctypes.c_void_p, operation: ctypes.c_void_p
    ) -> int:
        try:
            result = HRESULT()
            interface = ctypes.c_void_p()
            get_result = _com_method(
                operation,
                3,
                HRESULT,
                ctypes.POINTER(HRESULT),
                ctypes.POINTER(ctypes.c_void_p),
            )
            call_result = get_result(
                operation, ctypes.byref(result), ctypes.byref(interface)
            )
            self.activation_result = (
                int(call_result) if _failed(call_result) else int(result.value)
            )
            if not _failed(self.activation_result):
                self.audio_client = interface
        finally:
            self.completed.set()
        return S_OK


class _PcmConverter:
    def __init__(self, format_pointer: ctypes.c_void_p) -> None:
        raw = ctypes.cast(format_pointer, ctypes.POINTER(_WaveFormatEx)).contents
        self.channels = int(raw.nChannels)
        self.sample_rate = int(raw.nSamplesPerSec)
        self.bits = int(raw.wBitsPerSample)
        self.block_align = int(raw.nBlockAlign)
        tag = int(raw.wFormatTag)
        if tag == 0xFFFE and raw.cbSize >= 22:
            tag = ctypes.cast(
                int(format_pointer.value) + 24, ctypes.POINTER(wintypes.DWORD)
            ).contents.value
        self.format_tag = tag
        self._resample_buffer = np.empty(0, dtype=np.float64)
        self._resample_position = 0.0
        if self.channels < 1 or self.sample_rate < 1 or self.block_align < 1:
            raise ProcessAudioCaptureError("Windows 返回了无效的音频格式")
        if (self.format_tag, self.bits) not in {
            (1, 16),
            (1, 24),
            (1, 32),
            (3, 32),
        }:
            raise ProcessAudioCaptureError(
                f"暂不支持目标进程的音频格式（格式 {self.format_tag}，{self.bits} bit）"
            )

    def convert(self, data: bytes) -> bytes:
        if not data:
            return b""
        samples = self._samples(data)
        if self.channels > 1:
            usable = len(samples) - len(samples) % self.channels
            if usable <= 0:
                return b""
            samples = samples[:usable].reshape(-1, self.channels).mean(axis=1)
        samples = np.clip(samples, -32768, 32767)
        if self.sample_rate == 16_000:
            return samples.astype("<i2").tobytes()
        return self._resample(samples).astype("<i2").tobytes()

    def _resample(self, samples: np.ndarray) -> np.ndarray:
        if samples.size:
            self._resample_buffer = np.concatenate(
                (self._resample_buffer, samples.astype(np.float64, copy=False))
            )
        last_position = len(self._resample_buffer) - 1
        if self._resample_position > last_position:
            return np.empty(0, dtype=np.float64)
        step = self.sample_rate / 16_000
        count = int((last_position - self._resample_position) / step) + 1
        positions = self._resample_position + np.arange(count) * step
        converted = np.interp(
            positions,
            np.arange(len(self._resample_buffer)),
            self._resample_buffer,
        )
        self._resample_position = float(positions[-1] + step)
        consumed = max(0, int(self._resample_position) - 1)
        if consumed:
            self._resample_buffer = self._resample_buffer[consumed:]
            self._resample_position -= consumed
        return converted

    def _samples(self, data: bytes) -> np.ndarray:
        if self.format_tag == 3:
            values = np.frombuffer(data, dtype="<f4")
            return values.astype(np.float64) * 32767.0
        if self.bits == 16:
            return np.frombuffer(data, dtype="<i2").astype(np.float64)
        if self.bits == 32:
            values = np.frombuffer(data, dtype="<i4")
            return (values.astype(np.float64) / 65536.0)
        raw = np.frombuffer(data, dtype=np.uint8)
        usable = len(raw) - len(raw) % 3
        triples = raw[:usable].reshape(-1, 3).astype(np.int32)
        values = triples[:, 0] | (triples[:, 1] << 8) | (triples[:, 2] << 16)
        values = np.where(values & 0x800000, values - 0x1000000, values)
        return values.astype(np.float64) / 256.0


class WindowsProcessAudioCapture:
    """Capture one Windows process tree with the Application Loopback API."""

    def __init__(self) -> None:
        self._thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._ready_event = threading.Event()
        self._startup_error: BaseException | None = None
        self._state_lock = threading.Lock()

    @property
    def running(self) -> bool:
        thread = self._thread
        return thread is not None and thread.is_alive() and self._ready_event.is_set()

    def start(
        self,
        process_id: int,
        on_frame: Callable[[AudioFrame], None],
        *,
        include_process_tree: bool = True,
        on_error: Callable[[ProcessAudioCaptureError], None] | None = None,
    ) -> None:
        if sys.platform != "win32":
            raise ProcessAudioCaptureError("进程音频捕获只支持 Windows PC")
        if sys.getwindowsversion().build < 19041:
            raise ProcessAudioCaptureError(
                "进程音频捕获需要 Windows 10 2004（版本 19041）或更高版本"
            )
        if process_id <= 0:
            raise ProcessAudioCaptureError("请选择一个仍在运行的目标进程")
        with self._state_lock:
            if self._thread is not None and self._thread.is_alive():
                raise ProcessAudioCaptureError("已有进程音频捕获会话正在运行")
            self._stop_event.clear()
            self._ready_event.clear()
            self._startup_error = None
            self._thread = threading.Thread(
                target=self._capture_main,
                args=(process_id, include_process_tree, on_frame, on_error),
                name="process-audio-capture",
                daemon=True,
            )
            self._thread.start()
        if not self._ready_event.wait(8.0):
            self.stop()
            raise ProcessAudioCaptureError("初始化进程音频捕获超时")
        if self._startup_error is not None:
            error = self._startup_error
            self.stop()
            if isinstance(error, ProcessAudioCaptureError):
                raise error
            raise ProcessAudioCaptureError("无法初始化目标进程音频捕获") from error

    def stop(self) -> None:
        self._stop_event.set()
        thread = self._thread
        if thread is not None and thread is not threading.current_thread():
            thread.join(timeout=3.0)
        with self._state_lock:
            if self._thread is thread and (thread is None or not thread.is_alive()):
                self._thread = None

    def _capture_main(
        self,
        process_id: int,
        include_process_tree: bool,
        on_frame: Callable[[AudioFrame], None],
        on_error: Callable[[ProcessAudioCaptureError], None] | None,
    ) -> None:
        ole32 = ctypes.OleDLL("ole32")
        mmdevapi = ctypes.WinDLL("Mmdevapi")
        ole32.CoInitializeEx.argtypes = (ctypes.c_void_p, wintypes.DWORD)
        ole32.CoInitializeEx.restype = HRESULT
        ole32.CoUninitialize.argtypes = ()
        ole32.CoTaskMemFree.argtypes = (ctypes.c_void_p,)
        audio_client = ctypes.c_void_p()
        capture_client = ctypes.c_void_p()
        format_pointer = ctypes.c_void_p()
        requested_format = _WaveFormatEx(
            1,
            1,
            16_000,
            32_000,
            2,
            16,
            0,
        )
        format_owned_by_com = False
        started = False
        com_initialized = False
        process_handle = wintypes.HANDLE()
        try:
            result = ole32.CoInitializeEx(None, COINIT_MULTITHREADED)
            if _failed(result):
                raise ProcessAudioCaptureError(
                    f"初始化 Windows 音频组件失败（{_hex_hresult(result)}）"
                )
            com_initialized = True
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.OpenProcess.argtypes = (
                wintypes.DWORD,
                wintypes.BOOL,
                wintypes.DWORD,
            )
            kernel32.OpenProcess.restype = wintypes.HANDLE
            kernel32.WaitForSingleObject.argtypes = (
                wintypes.HANDLE,
                wintypes.DWORD,
            )
            kernel32.WaitForSingleObject.restype = wintypes.DWORD
            kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
            process_handle = kernel32.OpenProcess(
                SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
                False,
                process_id,
            )
            if not process_handle:
                raise ProcessAudioCaptureError("所选目标进程已经退出或无法访问")
            audio_client = self._activate(
                mmdevapi, process_id, include_process_tree
            )
            get_mix_format = _com_method(
                audio_client,
                8,
                HRESULT,
                ctypes.POINTER(ctypes.c_void_p),
            )
            result = get_mix_format(audio_client, ctypes.byref(format_pointer))
            stream_flags = AUDCLNT_STREAMFLAGS_LOOPBACK
            if _failed(result) or not format_pointer.value:
                # The process-loopback virtual device returns E_NOTIMPL for
                # GetMixFormat on some Windows builds. Shared-mode automatic
                # conversion lets us request the ASR-native format directly.
                format_pointer = ctypes.cast(
                    ctypes.pointer(requested_format), ctypes.c_void_p
                )
                stream_flags |= (
                    AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
                )
            else:
                format_owned_by_com = True
            converter = _PcmConverter(format_pointer)
            initialize = _com_method(
                audio_client,
                3,
                HRESULT,
                ctypes.c_int,
                wintypes.DWORD,
                REFERENCE_TIME,
                REFERENCE_TIME,
                ctypes.c_void_p,
                ctypes.c_void_p,
            )
            result = initialize(
                audio_client,
                AUDCLNT_SHAREMODE_SHARED,
                stream_flags,
                0,
                0,
                format_pointer,
                None,
            )
            if _failed(result):
                raise ProcessAudioCaptureError(
                    f"目标进程音频流初始化失败（{_hex_hresult(result)}）"
                )
            get_service = _com_method(
                audio_client,
                14,
                HRESULT,
                ctypes.POINTER(_GUID),
                ctypes.POINTER(ctypes.c_void_p),
            )
            result = get_service(
                audio_client,
                ctypes.byref(IID_IAUDIO_CAPTURE_CLIENT),
                ctypes.byref(capture_client),
            )
            if _failed(result):
                raise ProcessAudioCaptureError(
                    f"无法创建进程音频读取器（{_hex_hresult(result)}）"
                )
            start = _com_method(audio_client, 10, HRESULT)
            result = start(audio_client)
            if _failed(result):
                raise ProcessAudioCaptureError(
                    f"无法启动进程音频流（{_hex_hresult(result)}）"
                )
            started = True
            self._ready_event.set()
            self._read_packets(
                capture_client,
                converter,
                on_frame,
                kernel32,
                process_handle,
            )
        except BaseException as exc:
            if not self._ready_event.is_set():
                self._startup_error = exc
                self._ready_event.set()
            elif on_error is not None and not self._stop_event.is_set():
                error = (
                    exc
                    if isinstance(exc, ProcessAudioCaptureError)
                    else ProcessAudioCaptureError("进程音频捕获意外停止")
                )
                on_error(error)
        finally:
            if started and audio_client.value:
                try:
                    _com_method(audio_client, 11, HRESULT)(audio_client)
                except (OSError, ValueError):
                    pass
            if format_owned_by_com and format_pointer.value:
                ole32.CoTaskMemFree(format_pointer)
            _release_interface(capture_client)
            _release_interface(audio_client)
            if process_handle:
                kernel32.CloseHandle(process_handle)
            if com_initialized:
                ole32.CoUninitialize()

    @staticmethod
    def _activate(
        mmdevapi: ctypes.WinDLL,
        process_id: int,
        include_process_tree: bool,
    ) -> ctypes.c_void_p:
        activation = _AudioClientActivationParams()
        activation.ActivationType = 1
        activation.ProcessLoopbackParams.TargetProcessId = process_id
        activation.ProcessLoopbackParams.ProcessLoopbackMode = (
            PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            if include_process_tree
            else PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
        )
        variant = _PropVariant()
        variant.vt = VT_BLOB
        variant.blob = _Blob(ctypes.sizeof(activation), ctypes.addressof(activation))
        completion = _ActivationCompletion()
        operation = ctypes.c_void_p()
        activate = mmdevapi.ActivateAudioInterfaceAsync
        activate.argtypes = (
            wintypes.LPCWSTR,
            ctypes.POINTER(_GUID),
            ctypes.POINTER(_PropVariant),
            ctypes.POINTER(_CompletionObject),
            ctypes.POINTER(ctypes.c_void_p),
        )
        activate.restype = HRESULT
        try:
            result = activate(
                VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                ctypes.byref(IID_IAUDIO_CLIENT),
                ctypes.byref(variant),
                ctypes.byref(completion.object),
                ctypes.byref(operation),
            )
            if _failed(result):
                raise ProcessAudioCaptureError(
                    f"Windows 不接受进程音频捕获请求（{_hex_hresult(result)}）"
                )
            if not completion.completed.wait(7.0):
                raise ProcessAudioCaptureError("等待 Windows 创建进程音频流超时")
            if (
                _failed(completion.activation_result)
                or not completion.audio_client.value
            ):
                raise ProcessAudioCaptureError(
                    "Windows 无法捕获所选进程的输出音频"
                    f"（{_hex_hresult(completion.activation_result)}）"
                )
            return completion.audio_client
        finally:
            # The async-operation interface is useful only until
            # GetActivateResult has completed. Keeping it for the entire
            # capture lifetime lets some Windows builds invalidate it before
            # our capture thread exits, which made the late Release call
            # dereference a stale COM pointer.
            _release_interface(operation)

    def _read_packets(
        self,
        capture_client: ctypes.c_void_p,
        converter: _PcmConverter,
        on_frame: Callable[[AudioFrame], None],
        kernel32: ctypes.WinDLL,
        process_handle: wintypes.HANDLE,
    ) -> None:
        get_next_size = _com_method(
            capture_client, 5, HRESULT, ctypes.POINTER(wintypes.UINT)
        )
        get_buffer = _com_method(
            capture_client,
            3,
            HRESULT,
            ctypes.POINTER(ctypes.c_void_p),
            ctypes.POINTER(wintypes.UINT),
            ctypes.POINTER(wintypes.DWORD),
            ctypes.c_void_p,
            ctypes.c_void_p,
        )
        release_buffer = _com_method(
            capture_client, 4, HRESULT, wintypes.UINT
        )
        while not self._stop_event.is_set():
            if kernel32.WaitForSingleObject(process_handle, 0) == WAIT_OBJECT_0:
                raise ProcessAudioCaptureError("所选目标进程已经退出")
            packet_frames = wintypes.UINT()
            result = get_next_size(capture_client, ctypes.byref(packet_frames))
            if _failed(result):
                raise ProcessAudioCaptureError(
                    f"读取进程音频失败（{_hex_hresult(result)}）"
                )
            if not packet_frames.value:
                time.sleep(0.01)
                continue
            while packet_frames.value and not self._stop_event.is_set():
                data_pointer = ctypes.c_void_p()
                frames = wintypes.UINT()
                flags = wintypes.DWORD()
                result = get_buffer(
                    capture_client,
                    ctypes.byref(data_pointer),
                    ctypes.byref(frames),
                    ctypes.byref(flags),
                    None,
                    None,
                )
                if _failed(result):
                    raise ProcessAudioCaptureError(
                        f"取得进程音频缓冲失败（{_hex_hresult(result)}）"
                    )
                try:
                    byte_count = int(frames.value) * converter.block_align
                    native = (
                        bytes(byte_count)
                        if flags.value & AUDCLNT_BUFFERFLAGS_SILENT
                        else ctypes.string_at(data_pointer, byte_count)
                    )
                    pcm16 = converter.convert(native)
                    if pcm16:
                        on_frame(AudioFrame(pcm16=pcm16))
                finally:
                    release_buffer(capture_client, frames)
                result = get_next_size(capture_client, ctypes.byref(packet_frames))
                if _failed(result):
                    raise ProcessAudioCaptureError(
                        f"读取进程音频失败（{_hex_hresult(result)}）"
                    )
