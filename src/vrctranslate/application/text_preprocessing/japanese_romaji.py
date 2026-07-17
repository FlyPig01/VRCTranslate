from __future__ import annotations

import re

_HIRAGANA_MAP: dict[str, str] = {
    "kya": "きゃ", "kyu": "きゅ", "kyo": "きょ",
    "sha": "しゃ", "shu": "しゅ", "sho": "しょ",
    "cha": "ちゃ", "chu": "ちゅ", "cho": "ちょ",
    "nya": "にゃ", "nyu": "にゅ", "nyo": "にょ",
    "hya": "ひゃ", "hyu": "ひゅ", "hyo": "ひょ",
    "mya": "みゃ", "myu": "みゅ", "myo": "みょ",
    "rya": "りゃ", "ryu": "りゅ", "ryo": "りょ",
    "gya": "ぎゃ", "gyu": "ぎゅ", "gyo": "ぎょ",
    "ja": "じゃ", "ju": "じゅ", "jo": "じょ",
    "jya": "じゃ", "jyu": "じゅ", "jyo": "じょ",
    "bya": "びゃ", "byu": "びゅ", "byo": "びょ",
    "pya": "ぴゃ", "pyu": "ぴゅ", "pyo": "ぴょ",
    "shi": "し", "chi": "ち", "tsu": "つ",
    "ka": "か", "ki": "き", "ku": "く", "ke": "け", "ko": "こ",
    "sa": "さ", "si": "し", "su": "す", "se": "せ", "so": "そ",
    "ta": "た", "ti": "ち", "tu": "つ", "te": "て", "to": "と",
    "na": "な", "ni": "に", "nu": "ぬ", "ne": "ね", "no": "の",
    "ha": "は", "hi": "ひ", "fu": "ふ", "he": "へ", "ho": "ほ",
    "ma": "ま", "mi": "み", "mu": "む", "me": "め", "mo": "も",
    "ya": "や", "yu": "ゆ", "yo": "よ",
    "ra": "ら", "ri": "り", "ru": "る", "re": "れ", "ro": "ろ",
    "wa": "わ", "wi": "ゐ", "we": "ゑ", "wo": "を",
    "ga": "が", "gi": "ぎ", "gu": "ぐ", "ge": "げ", "go": "ご",
    "za": "ざ", "ji": "じ", "zu": "ず", "ze": "ぜ", "zo": "ぞ",
    "da": "だ", "di": "ぢ", "du": "づ", "de": "で", "do": "ど",
    "ba": "ば", "bi": "び", "bu": "ぶ", "be": "べ", "bo": "ぼ",
    "pa": "ぱ", "pi": "ぴ", "pu": "ぷ", "pe": "ぺ", "po": "ぽ",
    "a": "あ", "i": "い", "u": "う", "e": "え", "o": "お",
    "n": "ん",
    "vu": "ゔ",
}

_SOKUON_CONSONANTS = {"k", "s", "t", "p", "c", "g", "z", "d", "b"}


def _looks_like_romaji(text: str) -> bool:
    stripped = re.sub(r"[\s\d\W_]", "", text, flags=re.UNICODE)
    if not stripped:
        return False
    if not re.fullmatch(r"[a-zA-Z]+", stripped):
        return False
    lower = stripped.lower()
    words = re.findall(r"[a-z]+", text.lower())
    if not words:
        return False
    common_english = {
        "the", "is", "are", "was", "were", "and", "or", "not", "you", "have",
        "has", "that", "this", "with", "for", "from", "but", "all", "can",
        "will", "would", "could", "should", "about", "what", "when", "where",
        "who", "how", "why", "which", "their", "there", "they", "them",
        "then", "than", "some", "any", "very", "just", "only", "also",
    }
    english_hits = sum(1 for w in words if w in common_english)
    if english_hits >= 2:
        return False
    jp_patterns = ["nichiwa", "konnichi", "ohayou", "arigatou", "sumimasen",
                   "gomen", "sugoi", "kawaii", "baka", "chan", "kun",
                   "sensei", "desu", "masu", "mashita", "nani", "kore",
                   "sore", "dare", "itsu", "doko", "nandemo", "ittai",
                   "itte", "kimashita", "ikimasho", "tabemashou", "kyou",
                   "ashita", "kinou", "hayaku", "osoi", "kirei", "shiawase",
                   "kanashii", "ureshii", "shitsurei", "onegai", "yoroshiku",
                   "hajimemashite", "otsukaresama", "itadakimasu", "gozaimasu",
                   "kashikoi", "subarashii", "omoshiroi", "tsukareta",
                   "kitanai", "mazui", "oishii", "kireina", "kakkoii",
                   "kawaiikute", "tanoshii", "samishii", "atsui", "samui",
                   "muzukashii", "yasashii", "takai", "yasukute", "omoi",
                   "karui", "hageshii", "yowai", "tsuyoi", "hayai"]
    for pattern in jp_patterns:
        if pattern in lower:
            return True
    if english_hits >= 1 and len(words) >= 3:
        return False
    return False


def _should_convert(text: str, source_language: str) -> bool:
    if source_language == "ja":
        stripped = re.sub(r"[\s\d\W_]", "", text, flags=re.UNICODE)
        if not stripped:
            return False
        if re.search(r"[\u3040-\u309f\u30a0-\u30ff\u4e00-\u9fff]", stripped):
            return False
        return bool(re.fullmatch(r"[a-zA-Z]+", stripped)) or _looks_like_romaji(text)
    if source_language == "auto":
        return _looks_like_romaji(text)
    return False


def _convert_word(word: str) -> str:
    result: list[str] = []
    i = 0
    n = len(word)
    lower = word.lower()
    while i < n:
        if i + 1 < n and lower[i] == lower[i + 1] and lower[i] in _SOKUON_CONSONANTS:
            result.append("っ")
            i += 1
            continue
        matched = False
        for length in (3, 2, 1):
            if i + length <= n:
                chunk = lower[i:i + length]
                if chunk in _HIRAGANA_MAP:
                    if length == 1 and chunk == "n" and i + 1 < n:
                        next_char = lower[i + 1]
                        if next_char in "aeiouy":
                            continue
                    result.append(_HIRAGANA_MAP[chunk])
                    i += length
                    matched = True
                    break
        if not matched:
            result.append(word[i])
            i += 1
    return "".join(result)


def romaji_to_hiragana(text: str) -> str:
    parts = re.split(r"([a-zA-Z]+)", text)
    out: list[str] = []
    for part in parts:
        if part and part.isascii() and part.isalpha():
            out.append(_convert_word(part))
        else:
            out.append(part)
    return "".join(out)


def preprocess_romaji(text: str, source_language: str, enabled: bool = True) -> tuple[str, bool]:
    if not enabled:
        return text, False
    if not _should_convert(text, source_language):
        return text, False
    converted = romaji_to_hiragana(text)
    if converted == text:
        return text, False
    return converted, True
