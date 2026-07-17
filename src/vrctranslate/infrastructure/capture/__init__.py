"""Windows window enumeration and in-memory capture adapters."""
from vrctranslate.infrastructure.capture.capture_router import CaptureRouter
from vrctranslate.infrastructure.capture.mss_capture import MssScreenCapture
from vrctranslate.infrastructure.capture.windows_graphics_capture import WindowsGraphicsCapture

__all__ = ["CaptureRouter", "MssScreenCapture", "WindowsGraphicsCapture"]
