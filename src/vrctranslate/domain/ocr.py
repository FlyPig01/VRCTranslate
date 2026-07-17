from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class OcrText:
    text: str
    confidence: float
    box: tuple[tuple[int, int], ...] = ()


@dataclass(frozen=True, slots=True)
class CaptureRegion:
    x: int
    y: int
    width: int
    height: int


@dataclass(frozen=True, slots=True)
class WindowInfo:
    hwnd: int
    title: str
    left: int
    top: int
    width: int
    height: int
    process_name: str = ""
    process_id: int = 0

    @property
    def display_name(self) -> str:
        process = f" · {self.process_name}" if self.process_name else ""
        return f"{self.title}{process}"


@dataclass(frozen=True, slots=True)
class CapturedFrame:
    """An in-memory frame plus a small immutable change signature."""

    pixels: object
    signature: bytes
