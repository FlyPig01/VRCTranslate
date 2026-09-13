# Third-party notices

VRCTranslate (V2 native desktop app) uses the following third-party components.

## OpenAI Whisper model (bundled)

- Model: `ggml-base-q5_1.bin` (Whisper base, Q5_1 quantization, whisper.cpp GGML conversion)
- Purpose: offline Chinese, English, Japanese, and Korean speech recognition
- Original model and license: OpenAI Whisper, MIT License — <https://github.com/openai/whisper/blob/main/LICENSE>
- GGML conversion source: <https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin>
- whisper.cpp project: <https://github.com/ggml-org/whisper.cpp> (MIT License)
- License text: <https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE>

The desktop release ships this model file inside the application package under `Models/`. Whisper and whisper.cpp are MIT-licensed, which permits redistribution; this notice provides the required attribution. The runtime adapter is Whisper.net (also MIT): <https://github.com/sandrohanea/whisper.net>.

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

The release publishes self-contained copies of the Windows App SDK and the .NET runtime beside the application executable. The Windows AI components that ship with the App SDK deployment (onnxruntime, DirectML, Windows AI projections) are removed from the published output because the application never calls those APIs.
