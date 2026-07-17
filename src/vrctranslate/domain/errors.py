from __future__ import annotations


class VrcTranslateError(RuntimeError):
    """Base error safe to carry across application boundaries."""

    def __init__(self, user_message: str) -> None:
        super().__init__(user_message)
        self.user_message = user_message


class TranslationError(VrcTranslateError):
    def __init__(self, category: str, user_message: str) -> None:
        super().__init__(user_message)
        self.category = category


class OcrUnavailable(VrcTranslateError):
    pass


class CaptureTargetUnavailable(VrcTranslateError):
    pass


class ChatboxSendFailed(VrcTranslateError):
    pass


class SettingsError(VrcTranslateError):
    pass

