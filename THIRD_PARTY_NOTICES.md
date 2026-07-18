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
