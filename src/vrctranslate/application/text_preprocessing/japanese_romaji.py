from __future__ import annotations

import re
from dataclasses import dataclass

from vrctranslate.application.dto import ROMAJI_MODES
from vrctranslate.application.ports.romaji_converter import RomajiConverter


_WORD_PATTERN = re.compile(r"[A-Za-z]+(?:'[A-Za-z]+)*")
_PROTECTED_PATTERN = re.compile(
    r"https?://[^\s]+|www\.[^\s]+|"
    r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}|"
    r"[@#][A-Za-z0-9_]+|"
    r"\b[A-Za-z][A-Za-z0-9]*(?:[_-][A-Za-z0-9]+)+\b|"
    r"\b[A-Za-z]*\d[A-Za-z0-9_]*\b",
    re.IGNORECASE,
)
_LATIN_PATTERN = re.compile(r"[A-Za-z]")

_FIXED_PHRASES = {
    "abataa": "アバター",
    "e": "へ",
    "ibento": "イベント",
    "konnichiwa": "こんにちは",
    "konbanwa": "こんばんは",
    "koohii": "コーヒー",
    "nihai": "二杯",
    "o": "を",
    "onryou": "音量",
    "wa": "は",
    "waarudo": "ワールド",
}

_JAPANESE_CHARACTER_CLASS = r"\u3040-\u30ff\u3400-\u9fff"

# Exact English/chat tokens are deliberately conservative. A user can select
# force mode when a short ambiguous token is known to be Japanese romaji.
_COMMON_ENGLISH = frozenset(
    {
        "a", "about", "after", "all", "also", "am", "an", "and", "any",
        "are", "as", "at", "be", "because", "before", "but", "by", "can",
        "chat", "could", "day", "do", "does", "done", "for", "from", "game",
        "go", "good", "have", "hello", "help", "here", "hey", "hi", "how",
        "i", "if", "in", "is", "it", "join", "just", "know", "like", "lol",
        "machine", "me", "more", "my", "name", "new", "no", "not", "now",
        "of", "on", "one", "only", "or", "our", "party", "please", "room",
        "should", "some", "start", "stop", "thank", "thanks", "that", "the",
        "their", "them", "then", "there", "they", "this", "time", "to", "too",
        "very", "want", "was", "we", "were", "what", "when", "where", "which",
        "who", "why", "will", "with", "world", "would", "www", "yes", "you",
        "your",
    }
)

# These short spellings are also English words, but become Japanese particles
# when the surrounding sentence already contains high-confidence romaji.
_AMBIGUOUS_JAPANESE_TOKENS = frozenset({"no", "to"})

_STRONG_EXACT = frozenset(
    {
        "abataa", "arigatou", "asobou", "baka", "chan", "daijoubu",
        "dare", "desu", "doko", "fuji", "gakusei", "gomen",
        "hajimemashite", "hayaku", "henkou", "houhou", "ibento",
        "itadakimasu", "ja", "kawaii", "kinou", "konnichiwa",
        "konbanwa", "koohii", "kore", "kudasai", "kyou", "masu",
        "matcha", "minna", "nani", "ohayou", "onegai", "ore",
        "oshiete", "otsukaresama", "ryokou", "sensei", "shinjuku",
        "sore", "sugoi", "sumimasen", "tanoshikatta", "toukyou",
        "waarudo", "watashi", "yoroshiku", "zasshi",
    }
)

_STRONG_PARTS = (
    "arigat", "asob", "chou", "desu", "gakusei", "gozaimasu", "hajime",
    "issho", "itadaki", "kawaii", "konnichi", "mashita", "match", "onegai",
    "otukare", "otsukare", "ryo", "shiawase", "shinj", "shitsu", "sshi",
    "sumimasen", "tcha", "toukyou", "tsu", "ureshii", "watashi", "yoroshi",
)


@dataclass(frozen=True, slots=True)
class RomajiPreprocessResult:
    original: str
    text: str
    changed: bool
    confidence: float
    unparsed_segments: tuple[str, ...] = ()


def normalize_romaji_mode(value: object, default: str = "off") -> str:
    mode = str(value)
    return mode if mode in ROMAJI_MODES else default


def _protected_spans(text: str) -> tuple[tuple[int, int], ...]:
    return tuple(match.span() for match in _PROTECTED_PATTERN.finditer(text))


def _inside(span: tuple[int, int], protected: tuple[tuple[int, int], ...]) -> bool:
    start, end = span
    return any(start >= left and end <= right for left, right in protected)


def _looks_like_brand(word: str) -> bool:
    uppercase = sum(1 for character in word if character.isupper())
    return uppercase >= 2


def _strong_romaji(word: str) -> bool:
    lower = word.lower().replace("'", "")
    if lower in _STRONG_EXACT:
        return True
    if "'" in word:
        return True
    return any(part in lower for part in _STRONG_PARTS)


def _convert_word(word: str, converter: RomajiConverter) -> str | None:
    fixed = _FIXED_PHRASES.get(word.lower())
    if fixed is not None:
        return fixed
    converted = converter.to_hiragana(word)
    return None if _LATIN_PATTERN.search(converted) else converted


def _normalize_converted_sentence(text: str) -> str:
    """Make converted kana look like ordinary Japanese for translators."""
    japanese = _JAPANESE_CHARACTER_CLASS
    normalized = re.sub(
        rf"(?<=[{japanese}])\s*,\s*(?=[{japanese}])",
        "、",
        text,
    )
    normalized = re.sub(
        rf"(?<=[{japanese}])\s+(?=[{japanese}])",
        "",
        normalized,
    )
    terminal_punctuation = {".": "。", "?": "？", "!": "！"}
    return re.sub(
        rf"(?<=[{japanese}])([.?!])$",
        lambda match: terminal_punctuation[match.group(1)],
        normalized,
    )


def preprocess_romaji(
    text: str,
    source_language: str,
    mode: str,
    converter: RomajiConverter | None,
) -> RomajiPreprocessResult:
    """Conservatively convert continuous romaji spans without damaging English."""

    normalized_mode = normalize_romaji_mode(mode)
    if (
        converter is None
        or normalized_mode == "off"
        or source_language not in {"ja", "auto"}
        or not text
    ):
        return RomajiPreprocessResult(text, text, False, 0.0)

    protected = _protected_spans(text)
    matches = list(_WORD_PATTERN.finditer(text))
    candidates = [
        match
        for match in matches
        if not _inside(match.span(), protected) and not _looks_like_brand(match.group())
    ]
    if not candidates:
        return RomajiPreprocessResult(text, text, False, 0.0)

    strong_phrase = any(_strong_romaji(match.group()) for match in candidates)
    parts: list[str] = []
    unparsed: list[str] = []
    converted_count = 0
    cursor = 0

    for match in matches:
        parts.append(text[cursor:match.start()])
        word = match.group()
        is_protected = _inside(match.span(), protected) or _looks_like_brand(word)
        lower = word.lower()
        should_convert = normalized_mode == "force"
        if normalized_mode == "auto" and not is_protected:
            should_convert = (
                (
                    lower not in _COMMON_ENGLISH
                    or (strong_phrase and lower in _AMBIGUOUS_JAPANESE_TOKENS)
                )
                and (_strong_romaji(word) or strong_phrase)
            )

        if is_protected or not should_convert:
            parts.append(word)
        else:
            converted = _convert_word(word, converter)
            if converted is None:
                parts.append(word)
                unparsed.append(word)
            else:
                parts.append(converted)
                converted_count += 1
        cursor = match.end()

    parts.append(text[cursor:])
    converted_text = "".join(parts)
    if converted_count:
        converted_text = _normalize_converted_sentence(converted_text)
    changed = converted_text != text
    confidence = converted_count / max(1, len(candidates)) if changed else 0.0
    return RomajiPreprocessResult(
        text,
        converted_text,
        changed,
        confidence,
        tuple(unparsed),
    )
