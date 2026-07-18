from __future__ import annotations

import json
from dataclasses import replace

import httpx
import numpy as np
import pytest

from vrctranslate.application.dto import GlossarySettings, TranslationProfile
from vrctranslate.application.use_cases.translate_visual_frame import TranslateVisualFrame
from vrctranslate.domain.errors import TranslationError
from vrctranslate.domain.glossary import GlossaryEntry
from vrctranslate.domain.visual_translation import (
    VisualTextRegion,
    VisualTranslationRequest,
)
from vrctranslate.infrastructure.translation.multimodal_openai import (
    OpenAICompatibleVisualTranslator,
)
from vrctranslate.infrastructure.translation.visual_image import encode_visual_frame


def _profile() -> TranslationProfile:
    return TranslationProfile(
        provider="multimodal_openai",
        base_url="https://example.invalid/v1",
        api_key="test-key",
        model="vision-test",
        timeout_seconds=5,
    )


def _request(*, regions=()) -> VisualTranslationRequest:
    return VisualTranslationRequest(
        "request",
        b"jpeg-bytes",
        "image/jpeg",
        "ja",
        "zh-CN",
        regions,
    )


def _translator(handler) -> OpenAICompatibleVisualTranslator:
    transport = httpx.MockTransport(handler)
    return OpenAICompatibleVisualTranslator(
        lambda timeout: httpx.Client(transport=transport, timeout=timeout)
    )


def test_whole_image_request_uses_openai_image_message_and_parses_fenced_json() -> None:
    captured = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured.update(json.loads(request.content))
        return httpx.Response(
            200,
            request=request,
            json={
                "choices": [
                    {
                        "message": {
                            "content": '```json\n{"source_text":"こんにちは","translation":"你好"}\n```'
                        }
                    }
                ]
            },
        )

    result = _translator(handler).translate(_request(), _profile())

    assert result.original == "こんにちは"
    assert result.translated == "你好"
    content = captured["messages"][1]["content"]
    assert content[1]["image_url"]["url"].startswith(
        "data:image/jpeg;base64,"
    )
    assert captured["model"] == "vision-test"
    assert captured["max_tokens"] == 1024
    system_prompt = captured["messages"][0]["content"]
    assert "日语罗马音" in system_prompt
    assert "英文句子" in system_prompt
    assert "用户名" in system_prompt
    assert "URL" in system_prompt
    assert "无法确认" in system_prompt


def test_qwen_multimodal_translation_disables_thinking() -> None:
    captured = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured.update(json.loads(request.content))
        return httpx.Response(
            200,
            request=request,
            json={
                "choices": [
                    {
                        "message": {
                            "content": '{"source_text":"Hello","translation":"你好"}'
                        }
                    }
                ]
            },
        )

    profile = replace(_profile(), model="qwen3.5-flash")
    _translator(handler).translate(_request(), profile)

    assert captured["enable_thinking"] is False


def test_unsupported_reasoning_parameter_falls_back_and_is_remembered() -> None:
    payloads: list[dict[str, object]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.content)
        payloads.append(payload)
        if "enable_thinking" in payload:
            return httpx.Response(
                400,
                request=request,
                json={"error": {"message": "unknown parameter enable_thinking"}},
            )
        return httpx.Response(
            200,
            request=request,
            json={
                "choices": [
                    {
                        "message": {
                            "content": '{"source_text":"Hello","translation":"你好"}'
                        }
                    }
                ]
            },
        )

    translator = _translator(handler)
    profile = replace(_profile(), model="qwen-compatible")

    translator.translate(_request(), profile)
    translator.translate(_request(), profile)

    assert len(payloads) == 3
    assert "enable_thinking" in payloads[0]
    assert "enable_thinking" not in payloads[1]
    assert "enable_thinking" not in payloads[2]


def test_multimodal_request_uses_a_safe_minimum_timeout() -> None:
    observed_timeouts: list[float] = []

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            request=request,
            json={
                "choices": [
                    {
                        "message": {
                            "content": '{"source_text":"Hello","translation":"你好"}'
                        }
                    }
                ]
            },
        )

    transport = httpx.MockTransport(handler)

    def client_factory(timeout: float) -> httpx.Client:
        observed_timeouts.append(timeout)
        return httpx.Client(transport=transport, timeout=timeout)

    translator = OpenAICompatibleVisualTranslator(client_factory)
    translator.translate(_request(), _profile())

    assert observed_timeouts == [8.0]


@pytest.mark.parametrize(
    ("error_type", "category", "message_part"),
    [
        (httpx.ConnectTimeout, "network", "DNS"),
        (httpx.ReadTimeout, "service", "关闭模型思考"),
        (httpx.WriteTimeout, "network", "缩小识别区域"),
        (httpx.PoolTimeout, "service", "请求过多"),
    ],
)
def test_multimodal_timeout_reports_the_failed_stage(
    error_type,
    category: str,
    message_part: str,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise error_type("timed out", request=request)

    with pytest.raises(TranslationError) as caught:
        _translator(handler).translate(_request(), _profile())

    assert caught.value.category == category
    assert message_part in caught.value.user_message


def test_region_response_ignores_unknown_duplicate_and_empty_items() -> None:
    regions = (
        VisualTextRegion("r1", (10, 20, 100, 40)),
        VisualTextRegion("r2", (10, 50, 100, 70)),
    )

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            request=request,
            json={
                "choices": [
                    {
                        "message": {
                            "content": json.dumps(
                                {
                                    "regions": [
                                        {"id": "unknown", "translation": "忽略"},
                                        {"id": "r2", "source_text": "二", "translation": "第二"},
                                        {"id": "r2", "translation": "重复"},
                                        {"id": "r1", "source_text": "一", "translation": "第一"},
                                    ]
                                },
                                ensure_ascii=False,
                            )
                        }
                    }
                ]
            },
        )

    result = _translator(handler).translate(_request(regions=regions), _profile())

    assert [(item.region_id, item.translated) for item in result.regions] == [
        ("r1", "第一"),
        ("r2", "第二"),
    ]


@pytest.mark.parametrize(
    ("status", "category"),
    [(401, "authentication"), (429, "quota"), (500, "service")],
)
def test_http_errors_are_classified(status: int, category: str) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(status, request=request)

    with pytest.raises(TranslationError) as caught:
        _translator(handler).translate(_request(), _profile())

    assert caught.value.category == category


def test_visual_frame_encoding_resizes_and_marks_regions_without_files(tmp_path) -> None:
    pixels = np.zeros((1200, 2400, 3), dtype=np.uint8)
    encoded = encode_visual_frame(
        pixels,
        maximum_side=1200,
        quality=80,
        regions=(VisualTextRegion("r1", (200, 100, 600, 200)),),
    )

    assert encoded.mime_type == "image/jpeg"
    assert encoded.image_bytes.startswith(b"\xff\xd8")
    assert encoded.regions[0].bbox == (100, 50, 300, 100)
    assert list(tmp_path.iterdir()) == []


def test_visual_glossary_prefers_user_terms_and_is_bounded() -> None:
    class Repository:
        revision = 1

        def builtin_entries(self):
            return (
                GlossaryEntry(
                    "builtin",
                    "en",
                    "zh-CN",
                    "Avatar",
                    "虚拟形象",
                    builtin=True,
                ),
            )

        def user_entries(self):
            return (
                GlossaryEntry(
                    "user",
                    "en",
                    "zh-CN",
                    "Avatar",
                    "模型",
                ),
            )

    class Visual:
        def __init__(self):
            self.request = None

        def translate(self, request, profile):
            self.request = request
            return type("Result", (), {"translated": "ok"})()

    visual = Visual()
    use_case = TranslateVisualFrame(
        visual,  # type: ignore[arg-type]
        Repository(),  # type: ignore[arg-type]
        lambda: GlossarySettings(),
    )

    use_case.execute(replace(_request(), source_language="en"), _profile())

    assert [(item.source, item.target) for item in visual.request.glossary] == [
        ("Avatar", "模型")
    ]
