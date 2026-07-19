from __future__ import annotations

from dataclasses import dataclass
from typing import Literal


@dataclass(frozen=True, slots=True)
class SpeechComponentFile:
    kind: Literal["model", "runtime"]
    relative_path: str
    url: str
    sha256: str
    size: int
    wheel: bool = False
    fallback_urls: tuple[str, ...] = ()


MODEL_ID = "sensevoice-small-int8"
MODEL_VERSION = "SenseVoiceSmall-2024-07-17"
RUNTIME_VERSION = "sherpa-onnx-1.13.4"
# Download archives plus a complete staging installation coexist briefly.
INSTALL_STAGING_BYTES = 300_000_000

SPEECH_COMPONENT_FILES = (
    SpeechComponentFile(
        "model",
        "model.int8.onnx",
        (
            "https://huggingface.co/csukuangfj/"
            "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/"
            "resolve/main/model.int8.onnx?download=true"
        ),
        "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51",
        239_233_841,
        fallback_urls=(
            "https://hf-mirror.com/csukuangfj/"
            "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/"
            "resolve/main/model.int8.onnx?download=true",
        ),
    ),
    SpeechComponentFile(
        "model",
        "tokens.txt",
        (
            "https://huggingface.co/csukuangfj/"
            "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/"
            "resolve/main/tokens.txt?download=true"
        ),
        "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc",
        315_894,
        fallback_urls=(
            "https://hf-mirror.com/csukuangfj/"
            "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/"
            "resolve/main/tokens.txt?download=true",
        ),
    ),
    SpeechComponentFile(
        "runtime",
        "sherpa_onnx-1.13.4-cp311-cp311-win_amd64.whl",
        (
            "https://files.pythonhosted.org/packages/7e/b1/"
            "ae1c113ac9c67dcabbed559f50950c7220a62e49e6b5acb4c2219ab22409/"
            "sherpa_onnx-1.13.4-cp311-cp311-win_amd64.whl"
        ),
        "04a82d79c13a4ce2bd9ccf51de93e83cbfc7bc50520c53e5e967565100d0724d",
        2_239_901,
        wheel=True,
    ),
    SpeechComponentFile(
        "runtime",
        "sherpa_onnx_core-1.13.4-py3-none-win_amd64.whl",
        (
            "https://files.pythonhosted.org/packages/95/b0/"
            "c3d59ac76f3db873e41bd0cb4fc30b352a278da3289217985aaae3650211/"
            "sherpa_onnx_core-1.13.4-py3-none-win_amd64.whl"
        ),
        "0a6949cf0fd83adb9fbcfdf5c27b8907a57f7b48626db703c7f6037be9b61764",
        16_450_053,
        wheel=True,
    ),
)


def component_download_size() -> int:
    return sum(item.size for item in SPEECH_COMPONENT_FILES)
