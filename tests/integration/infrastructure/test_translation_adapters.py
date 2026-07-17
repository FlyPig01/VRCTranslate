import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.translation.echo_translator import EchoTranslator
from vrctranslate.infrastructure.translation.router import TranslationRouter


def test_echo_translator_is_explicitly_a_test_echo() -> None:
    request = TranslationRequest("1", "hello", "en", "zh-CN")
    result = EchoTranslator().translate(request, TranslationProfile())
    assert result.translated == "[测试回显 zh-CN] hello"


def test_router_selects_explicit_adapter_without_ui_knowledge() -> None:
    request = TranslationRequest("1", "hello", "en", "zh-CN")
    result = TranslationRouter([EchoTranslator()]).translate(
        request, TranslationProfile()
    )
    assert result.translated.startswith("[测试回显")


def test_router_rejects_unknown_provider_instead_of_echo_fallback() -> None:
    request = TranslationRequest("1", "hello", "en", "zh-CN")
    with pytest.raises(TranslationError, match="未知翻译服务"):
        TranslationRouter([EchoTranslator()]).translate(
            request, TranslationProfile(provider="missing")
        )
