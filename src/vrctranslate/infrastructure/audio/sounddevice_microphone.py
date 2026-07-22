from __future__ import annotations

from collections.abc import Callable
from threading import RLock
from typing import Any

from vrctranslate.domain.speech import (
    AudioFrame,
    MicrophoneCaptureError,
    MicrophoneDevice,
)
from vrctranslate.infrastructure.speech.common import resample_pcm16


class SoundDeviceMicrophoneCapture:
    """Capture one selected input device as short in-memory 16 kHz PCM frames."""

    def __init__(self, sounddevice_module: Any | None = None) -> None:
        self._sounddevice = sounddevice_module
        self._stream: Any | None = None
        self._lock = RLock()
        self._error_reported = False

    @property
    def running(self) -> bool:
        with self._lock:
            return self._stream is not None

    def list_devices(self) -> list[MicrophoneDevice]:
        sounddevice = self._module()
        try:
            devices = sounddevice.query_devices()
            default_input = int(sounddevice.default.device[0])
            host_apis = sounddevice.query_hostapis()
        except Exception as exc:
            raise MicrophoneCaptureError("无法读取Windows麦克风设备") from exc
        default_name = ""
        if 0 <= default_input < len(devices):
            default_name = _normalized_device_name(str(devices[default_input]["name"]))
        preferred_defaults: list[tuple[int, int]] = []
        for item in host_apis:
            try:
                priority = _host_api_priority(str(item["name"]))
                index = int(item.get("default_input_device", -1))
            except (AttributeError, KeyError, TypeError, ValueError):
                continue
            if priority < 99 and index >= 0:
                preferred_defaults.append((priority, index))
        preferred_default = min(preferred_defaults)[1] if preferred_defaults else -1

        candidates: list[tuple[int, int, str, str]] = []
        for index, item in enumerate(devices):
            try:
                channels = int(item["max_input_channels"])
                name = str(item["name"]).strip()
                host_api_index = int(item["hostapi"])
                host_api = str(host_apis[host_api_index]["name"]).strip()
            except (KeyError, TypeError, ValueError):
                continue
            if (
                channels <= 0
                or not name
                or _is_windows_audio_alias(name)
                or _host_api_priority(host_api) >= 99
            ):
                continue
            candidates.append(
                (
                    _host_api_priority(host_api),
                    index,
                    name,
                    host_api,
                )
            )

        # The same physical endpoint is normally exposed through WASAPI,
        # DirectSound and MME. Keep the best Windows API representation only.
        output: list[MicrophoneDevice] = []
        seen: set[str] = set()
        for _, index, name, host_api in sorted(candidates):
            normalized = _normalized_device_name(name)
            if normalized in seen:
                continue
            seen.add(normalized)
            output.append(
                MicrophoneDevice(
                    str(index),
                    name,
                    (
                        index == preferred_default
                        if preferred_default >= 0
                        else normalized == default_name
                    ),
                    host_api,
                )
            )
        output.sort(key=lambda item: (not item.is_default, item.name.casefold()))
        return output

    def resolve_device_id(self, device_id: str) -> str:
        value = str(device_id).strip()
        if not value:
            return ""
        devices = self.list_devices()
        if any(item.id == value for item in devices):
            return value
        sounddevice = self._module()
        try:
            legacy = sounddevice.query_devices(int(value))
            legacy_name = _normalized_device_name(str(legacy["name"]))
        except Exception:
            return ""
        return next(
            (
                item.id
                for item in devices
                if _normalized_device_name(item.name) == legacy_name
            ),
            "",
        )

    def start(
        self,
        device_id: str,
        on_frame: Callable[[AudioFrame], None],
        *,
        on_error: Callable[[MicrophoneCaptureError], None] | None = None,
    ) -> None:
        with self._lock:
            if self._stream is not None:
                return
        sounddevice = self._module()
        device: int | None
        try:
            resolved = self.resolve_device_id(device_id)
            if not resolved:
                default_device = next(
                    (item for item in self.list_devices() if item.is_default),
                    None,
                )
                resolved = default_device.id if default_device is not None else ""
            device = int(resolved) if resolved else None
        except ValueError as exc:
            raise MicrophoneCaptureError("保存的麦克风设备已失效，请重新选择") from exc
        try:
            info = sounddevice.query_devices(device, "input")
            sample_rate = int(float(info["default_samplerate"]))
            if sample_rate <= 0:
                sample_rate = 48_000
            block_size = max(1, round(sample_rate * 0.1))
            self._error_reported = False

            def callback(
                indata: object,
                _frames: int,
                _time_info: object,
                _status: object,
            ) -> None:
                try:
                    payload = resample_pcm16(bytes(indata), sample_rate, 16_000)
                    if payload:
                        on_frame(AudioFrame(payload, 16_000, 1))
                except Exception as exc:
                    self._report_error(on_error, exc)

            stream = sounddevice.RawInputStream(
                samplerate=sample_rate,
                blocksize=block_size,
                device=device,
                channels=1,
                dtype="int16",
                callback=callback,
            )
            stream.start()
        except MicrophoneCaptureError:
            raise
        except Exception as exc:
            raise MicrophoneCaptureError(
                "麦克风无法启动，请检查设备、权限或独占模式"
            ) from exc
        with self._lock:
            if self._stream is not None:
                try:
                    stream.abort()
                    stream.close()
                except Exception:
                    pass
                return
            self._stream = stream

    def stop(self) -> None:
        with self._lock:
            stream, self._stream = self._stream, None
        if stream is None:
            return
        try:
            stream.abort()
        except Exception:
            pass
        try:
            stream.close()
        except Exception:
            pass

    def _module(self) -> Any:
        if self._sounddevice is None:
            try:
                import sounddevice
            except (ImportError, OSError) as exc:
                raise MicrophoneCaptureError("麦克风运行库未安装或无法加载") from exc
            self._sounddevice = sounddevice
        return self._sounddevice

    def _report_error(
        self,
        callback: Callable[[MicrophoneCaptureError], None] | None,
        error: Exception,
    ) -> None:
        with self._lock:
            if self._error_reported:
                return
            self._error_reported = True
        if callback is not None:
            callback(MicrophoneCaptureError("麦克风采集发生错误，请重新启用"))


def _normalized_device_name(value: str) -> str:
    return " ".join(value.casefold().replace("（", "(").replace("）", ")").split())


def _host_api_priority(value: str) -> int:
    folded = value.casefold()
    if "wasapi" in folded:
        return 0
    if "directsound" in folded:
        return 1
    if folded == "mme" or "mme" in folded:
        return 2
    return 99


def _is_windows_audio_alias(value: str) -> bool:
    folded = value.casefold()
    return any(
        marker in folded
        for marker in (
            "sound mapper",
            "声音映射器",
            "primary sound capture",
            "主声音捕获驱动",
        )
    )
