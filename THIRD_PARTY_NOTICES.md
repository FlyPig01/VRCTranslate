# Third-party notices

VRCTranslate (native desktop app) uses the following third-party components.

## SenseVoiceSmall model (bundled)

- Model: `model.int8.onnx` (SenseVoiceSmall, INT8 quantization) with its `tokens.txt` table
- Purpose: offline Chinese (including Cantonese-accented speech), English, Japanese, and Korean speech recognition
- Upstream model: FunAudioLLM/SenseVoiceSmall — <https://huggingface.co/FunAudioLLM/SenseVoiceSmall>
- ONNX export used here: <https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17>
- Project and license (MIT License, Copyright (c) 2025 FunASR): <https://github.com/FunAudioLLM/SenseVoice/blob/main/LICENSE>

The desktop release ships this payload inside the application package under `程序文件/Models/sensevoice/`. SenseVoice is MIT-licensed, which permits redistribution; this notice provides the required attribution.

## pyannote segmentation 3.0 model (bundled)

- Model: `segmentation.int8.onnx`, converted from `pyannote/segmentation-3.0`
- Purpose: locating speaker-change points so one captured sentence can be split between speakers
- Upstream model: <https://huggingface.co/pyannote/segmentation-3.0>
- ONNX conversion used here: <https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0>
- License (MIT License, Copyright (c) 2022 CNRS): the LICENSE file shipped in the conversion repository

The desktop release ships this model inside the application package under `程序文件/Models/speaker/`.

## 3D-Speaker CAM++ speaker-embedding model (bundled)

- Model: `embedding.onnx` (`3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx`)
- Purpose: extracting a voiceprint for each captured sentence so captions can carry a speaker label
- Conversion source: <https://huggingface.co/csukuangfj/speaker-embedding-models>
- Project and license (Apache License 2.0): <https://github.com/modelscope/3D-Speaker>

The desktop release ships this model inside the application package under `程序文件/Models/speaker/`. Both speaker models are used only when the user enables speaker labels; the voiceprints derived from them stay on the local machine.

## sherpa-onnx (bundled)

- Packages: `org.k2fsa.sherpa.onnx` 1.13.8 (managed API) and `org.k2fsa.sherpa.onnx.runtime.win-x64` 1.13.8 (native `sherpa-onnx-c-api.dll` and the ONNX Runtime build it links)
- Purpose: local SenseVoice inference — fbank features, CTC decoding, inverse text normalization
- Project and license (Apache License 2.0): <https://github.com/k2-fsa/sherpa-onnx/blob/master/LICENSE>

## ONNX Runtime (bundled)

- Component: `onnxruntime.dll`, redistributed through the sherpa-onnx win-x64 runtime package
- Purpose: CPU inference engine for the local SenseVoice model
- Project and license (MIT License): <https://github.com/microsoft/onnxruntime/blob/main/LICENSE>

## NAudio (bundled)

- Packages: `NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM` 2.2.1
- Purpose: microphone and system-loopback audio capture for local speech recognition
- Project: <https://github.com/naudio/NAudio>
- License: MIT License
- License text: <https://github.com/naudio/NAudio/blob/master/license.txt>

The granular packages are used instead of the NAudio metapackage so the WinForms-oriented assemblies (and the Windows Desktop runtime they require) stay out of the published application.

## Microsoft Windows App SDK and .NET (bundled)

- Purpose: WinUI 3 desktop framework and self-contained .NET 10 runtime
- Windows App SDK license: <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- Windows App SDK notice: <https://github.com/microsoft/WindowsAppSDK/blob/main/NOTICE.md>
- .NET runtime license: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- .NET third-party notices: <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>

The release publishes self-contained copies of the Windows App SDK and the .NET runtime beside the application executable. The Windows AI components that ship with the App SDK deployment (DirectML and the Windows AI projections, plus the App SDK copy of `onnxruntime.dll`) are removed from the published output because the application never calls those APIs; the ONNX Runtime build shipped for SenseVoice comes from the sherpa-onnx runtime package instead.
