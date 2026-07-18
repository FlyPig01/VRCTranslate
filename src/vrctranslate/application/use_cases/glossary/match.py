from __future__ import annotations

import re
import unicodedata

from vrctranslate.domain.glossary import (
    GlossaryEntry,
    GlossaryMatch,
    normalize_glossary_text,
)


_KANA = re.compile(r"[\u3040-\u30ff]")
_HANGUL = re.compile(r"[\uac00-\ud7af]")
_LATIN_TERM = re.compile(r"^[A-Za-z0-9 _.'+-]+$")


def _target_matches(entry: GlossaryEntry, target_language: str) -> bool:
    return entry.target_language in {"any", target_language}


def _auto_entries(entries: tuple[GlossaryEntry, ...]) -> tuple[GlossaryEntry, ...]:
    groups: dict[str, list[GlossaryEntry]] = {}
    for entry in entries:
        groups.setdefault(normalize_glossary_text(entry.source), []).append(entry)
    selected: list[GlossaryEntry] = []
    for group in groups.values():
        source = group[0].source
        if any(entry.source_language == "any" for entry in group):
            selected.extend(entry for entry in group if entry.source_language == "any")
            continue
        if _KANA.search(source):
            selected.extend(entry for entry in group if entry.source_language == "ja")
            continue
        if _HANGUL.search(source):
            selected.extend(entry for entry in group if entry.source_language == "ko")
            continue
        targets = {normalize_glossary_text(entry.target) for entry in group}
        if len(targets) == 1:
            selected.append(
                sorted(group, key=lambda entry: (entry.builtin, entry.id))[0]
            )
    return tuple(selected)


def _normalized_with_map(text: str, *, case_sensitive: bool) -> tuple[str, list[int]]:
    output: list[str] = []
    positions: list[int] = []
    for index, character in enumerate(text):
        normalized = unicodedata.normalize("NFKC", character)
        if not case_sensitive:
            normalized = normalized.casefold()
        output.extend(normalized)
        positions.extend([index] * len(normalized))
    return "".join(output), positions


def _candidate_spans(text: str, entry: GlossaryEntry) -> list[tuple[int, int]]:
    normalized, positions = _normalized_with_map(
        text,
        case_sensitive=entry.case_sensitive,
    )
    needle = normalize_glossary_text(
        entry.source,
        case_sensitive=entry.case_sensitive,
    )
    if not needle or not positions:
        return []
    spans: list[tuple[int, int]] = []
    cursor = 0
    while (found := normalized.find(needle, cursor)) >= 0:
        normalized_end = found + len(needle)
        start = positions[found]
        end = positions[normalized_end - 1] + 1
        if _LATIN_TERM.fullmatch(entry.source):
            before = text[start - 1] if start else ""
            after = text[end] if end < len(text) else ""
            if (
                before and (before.isalnum() or before == "_")
            ) or (
                after and (after.isalnum() or after == "_")
            ):
                cursor = found + 1
                continue
        spans.append((start, end))
        cursor = normalized_end
    return spans


def match_glossary(
    text: str,
    entries: tuple[GlossaryEntry, ...],
    source_language: str,
    target_language: str,
    purpose: str,
    *,
    user_only: bool = False,
    limit: int = 32,
) -> tuple[GlossaryMatch, ...]:
    del purpose  # All terms apply to both self messages and OCR.
    eligible = tuple(
        entry
        for entry in entries
        if _target_matches(entry, target_language)
        and (not user_only or not entry.builtin)
    )
    if source_language == "auto":
        eligible = _auto_entries(eligible)
    else:
        eligible = tuple(
            entry
            for entry in eligible
            if entry.source_language in {"any", source_language}
        )

    candidates: list[GlossaryMatch] = []
    for entry in eligible:
        for start, end in _candidate_spans(text, entry):
            candidates.append(GlossaryMatch(start, end, text[start:end], entry))
    candidates.sort(
        key=lambda match: (
            match.start,
            match.entry.builtin,
            -(match.end - match.start),
            match.entry.id,
        )
    )
    selected: list[GlossaryMatch] = []
    occupied_until = -1
    for candidate in candidates:
        if candidate.start < occupied_until:
            continue
        selected.append(candidate)
        occupied_until = candidate.end
        if len(selected) >= limit:
            break
    return tuple(selected)
