"""Translation settings components.

The package keeps profile editing, routing and local-model management isolated while
``TranslationSettingsPage`` remains the public facade used by the rest of the UI.
"""

from vrctranslate.presentation.qt.pages.settings.translation.page import (
    TranslationSettingsPage,
)

__all__ = ["TranslationSettingsPage"]
