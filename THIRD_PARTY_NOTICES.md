# Third-party notices

VRCTranslate uses the following third-party component in addition to the dependencies declared in `pyproject.toml`.

## wanakana-python

- Version: 1.2.2
- Purpose: Japanese romaji-to-kana conversion rules
- Project: <https://github.com/starwort/wanakana>
- License: Mozilla Public License 2.0 (MPL-2.0)
- License text: <https://www.mozilla.org/MPL/2.0/>

The component remains under its own license. VRCTranslate adds compatibility handling in its own adapter and does not modify the installed `wanakana-python` package.

## Pillow

- Version range: 10.x to 12.x
- Purpose: in-memory OCR frame resizing, annotation and JPEG/PNG encoding
- Project: <https://github.com/python-pillow/Pillow>
- License: MIT-CMU
- License text: <https://github.com/python-pillow/Pillow/blob/main/LICENSE>

VRCTranslate uses Pillow only for in-memory image processing. It does not add a screenshot-saving path.

## websocket-client

- Version range: 1.8.x to 1.x
- Purpose: WebSocket transport for Tencent Cloud and Alibaba Cloud NLS realtime speech recognition
- Project: <https://github.com/websocket-client/websocket-client>
- License: Apache License 2.0
- License text: <https://www.apache.org/licenses/LICENSE-2.0>

VRCTranslate implements each provider's documented authentication and realtime ASR framing on top of `websocket-client`. No provider SDK or file-transcription helper is bundled, and captured PCM remains in memory.

## Alibaba Cloud Machine Translation SDK

- Package: `alibabacloud-alimt20181012`
- Version range: 1.5.2 to 1.x
- Purpose: authenticated calls to Alibaba Cloud `TranslateGeneral` and `Translate`
- Project: <https://github.com/aliyun/alibabacloud-python-sdk>
- License: Apache License 2.0
- License text: <https://www.apache.org/licenses/LICENSE-2.0>

The SDK and its Tea runtime dependencies are used only by Alibaba Cloud text translation profiles. Realtime Alibaba Cloud NLS speech recognition continues to use its dedicated WebSocket protocol.
