from __future__ import annotations

import ctypes
import os
from pathlib import Path
from ctypes import wintypes

from vrctranslate.domain.ocr import WindowInfo


class WindowsApi:
    def __init__(self) -> None:
        self._user32 = ctypes.WinDLL("user32", use_last_error=True)
        self._kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self._current_process_id = os.getpid()
        self._user32.GetWindowThreadProcessId.argtypes = [
            wintypes.HWND,
            ctypes.POINTER(wintypes.DWORD),
        ]
        self._user32.GetWindowThreadProcessId.restype = wintypes.DWORD
        self._kernel32.OpenProcess.argtypes = [
            wintypes.DWORD,
            wintypes.BOOL,
            wintypes.DWORD,
        ]
        self._kernel32.OpenProcess.restype = wintypes.HANDLE
        self._kernel32.QueryFullProcessImageNameW.argtypes = [
            wintypes.HANDLE,
            wintypes.DWORD,
            wintypes.LPWSTR,
            ctypes.POINTER(wintypes.DWORD),
        ]
        self._kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self._kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        self._kernel32.CloseHandle.restype = wintypes.BOOL

    def enable_dpi_awareness(self) -> None:
        try:
            self._user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
        except (AttributeError, OSError):
            try:
                self._user32.SetProcessDPIAware()
            except (AttributeError, OSError):
                pass

    def exclude_from_capture(self, hwnd: int) -> bool:
        """Ask Windows 10 2004+ to omit an application window from capture."""
        wda_exclude_from_capture = 0x00000011
        try:
            return bool(
                self._user32.SetWindowDisplayAffinity(
                    wintypes.HWND(hwnd), wintypes.DWORD(wda_exclude_from_capture)
                )
            )
        except (AttributeError, OSError):
            return False

    def activate_window(self, hwnd: int) -> bool:
        """Foreground a target without changing its maximized/fullscreen state."""

        try:
            handle = wintypes.HWND(hwnd)
            # SW_RESTORE also turns a maximized window back into a normal window.
            # It is only appropriate for a genuinely minimized target.
            if self._user32.IsIconic(handle):
                self._user32.ShowWindow(handle, 9)  # SW_RESTORE
            return bool(self._user32.SetForegroundWindow(handle))
        except (AttributeError, OSError):
            return False

    def is_foreground_window(self, hwnd: int) -> bool:
        try:
            return int(self._user32.GetForegroundWindow() or 0) == int(hwnd)
        except (AttributeError, OSError, TypeError, ValueError):
            return False

    def is_window_minimized(self, hwnd: int) -> bool:
        try:
            return bool(self._user32.IsIconic(wintypes.HWND(hwnd)))
        except (AttributeError, OSError):
            return False

    def list_windows(self) -> list[WindowInfo]:
        windows: list[WindowInfo] = []
        callback_type = ctypes.WINFUNCTYPE(
            wintypes.BOOL, wintypes.HWND, wintypes.LPARAM
        )

        @callback_type
        def callback(hwnd: int, _lparam: int) -> bool:
            if not self._user32.IsWindowVisible(hwnd):
                return True
            length = self._user32.GetWindowTextLengthW(hwnd)
            if length <= 0:
                return True
            process_id = self._window_process_id(hwnd)
            if process_id == self._current_process_id:
                return True
            window = self.get_window(hwnd)
            if window and window.title and window.width > 100 and window.height > 100:
                windows.append(window)
            return True

        self._user32.EnumWindows(callback, 0)
        windows.sort(
            key=lambda item: (
                "vrchat" not in (item.title + item.process_name).lower(),
                item.title.lower(),
            )
        )
        return windows

    def get_window(self, hwnd: int) -> WindowInfo | None:
        rect = wintypes.RECT()
        if not self._user32.GetClientRect(hwnd, ctypes.byref(rect)):
            return None
        top_left = wintypes.POINT(rect.left, rect.top)
        bottom_right = wintypes.POINT(rect.right, rect.bottom)
        if not self._user32.ClientToScreen(hwnd, ctypes.byref(top_left)):
            return None
        if not self._user32.ClientToScreen(hwnd, ctypes.byref(bottom_right)):
            return None
        width = bottom_right.x - top_left.x
        height = bottom_right.y - top_left.y
        if width <= 0 or height <= 0:
            return None
        length = self._user32.GetWindowTextLengthW(hwnd)
        buffer = ctypes.create_unicode_buffer(length + 1)
        self._user32.GetWindowTextW(hwnd, buffer, length + 1)
        process_id = self._window_process_id(hwnd)
        return WindowInfo(
            hwnd,
            buffer.value,
            top_left.x,
            top_left.y,
            width,
            height,
            self._process_name(process_id),
            process_id,
        )

    def virtual_desktop(self) -> WindowInfo:
        """Return the physical bounds of the complete Windows desktop."""

        sm_xvirtualscreen = 76
        sm_yvirtualscreen = 77
        sm_cxvirtualscreen = 78
        sm_cyvirtualscreen = 79
        left = int(self._user32.GetSystemMetrics(sm_xvirtualscreen))
        top = int(self._user32.GetSystemMetrics(sm_yvirtualscreen))
        width = int(self._user32.GetSystemMetrics(sm_cxvirtualscreen))
        height = int(self._user32.GetSystemMetrics(sm_cyvirtualscreen))
        if width <= 0 or height <= 0:
            width = int(self._user32.GetSystemMetrics(0))
            height = int(self._user32.GetSystemMetrics(1))
            left = top = 0
        return WindowInfo(0, "Desktop", left, top, width, height)

    def _window_process_id(self, hwnd: int) -> int:
        process_id = wintypes.DWORD()
        self._user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
        return int(process_id.value)

    def _process_name(self, process_id: int) -> str:
        if not process_id:
            return ""
        process_query_limited_information = 0x1000
        handle = self._kernel32.OpenProcess(
            process_query_limited_information, False, process_id
        )
        if not handle:
            return ""
        try:
            size = wintypes.DWORD(32768)
            buffer = ctypes.create_unicode_buffer(size.value)
            if not self._kernel32.QueryFullProcessImageNameW(
                handle, 0, buffer, ctypes.byref(size)
            ):
                return ""
            return Path(buffer.value).name
        finally:
            self._kernel32.CloseHandle(handle)
