from __future__ import annotations

from vrctranslate.domain.glossary import GlossaryEntry


def merge_glossary_entries(
    builtin_entries: tuple[GlossaryEntry, ...],
    user_entries: tuple[GlossaryEntry, ...],
    *,
    builtin_enabled: bool,
) -> tuple[GlossaryEntry, ...]:
    merged = {
        entry.conflict_key: entry
        for entry in builtin_entries
        if builtin_enabled
    }
    for entry in user_entries:
        merged[entry.conflict_key] = entry
    return tuple(merged.values())
