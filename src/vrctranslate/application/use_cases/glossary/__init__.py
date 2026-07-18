from vrctranslate.application.use_cases.glossary.match import match_glossary
from vrctranslate.application.use_cases.glossary.merge import merge_glossary_entries
from vrctranslate.application.use_cases.glossary.protect import (
    GlossaryProtection,
    protect_matches,
)

__all__ = [
    "GlossaryProtection",
    "match_glossary",
    "merge_glossary_entries",
    "protect_matches",
]
