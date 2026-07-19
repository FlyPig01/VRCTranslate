from vrctranslate.infrastructure.speech.aliyun_nls_realtime import (
    AliyunNlsRealtimeSpeechRecognizer,
)
from vrctranslate.infrastructure.speech.router import SpeechRecognitionRouter
from vrctranslate.infrastructure.speech.sensevoice_local import (
    SenseVoiceLocalSpeechRecognizer,
)
from vrctranslate.infrastructure.speech.tencent_realtime import (
    TencentRealtimeSpeechRecognizer,
)

__all__ = [
    "AliyunNlsRealtimeSpeechRecognizer",
    "SpeechRecognitionRouter",
    "SenseVoiceLocalSpeechRecognizer",
    "TencentRealtimeSpeechRecognizer",
]
