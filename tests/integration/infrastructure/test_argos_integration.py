from __future__ import annotations

import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.paths import discover_app_paths
from vrctranslate.infrastructure.translation.argos_model_manager import ArgosModelManager
from vrctranslate.infrastructure.translation.argos_translator import ArgosTranslator


pytestmark = pytest.mark.argos


def test_real_argos_english_to_chinese_when_model_is_installed() -> None:
    manager = ArgosModelManager(discover_app_paths())
    if not manager.component_available:
        pytest.skip("Argos component is not installed")
    installed = {
        (model.source_language, model.target_language)
        for model in manager.installed_models()
    }
    if ("en", "zh") not in installed:
        pytest.skip("Argos en -> zh model is not installed")
    result = ArgosTranslator(manager).translate(
        TranslationRequest("1", "Hello", "en", "zh-CN"),
        TranslationProfile(provider="argos"),
    )
    assert result.translated
    assert result.translated != "Hello"
