"""手动裁判用：中日互译 + 罗马音转假名 翻译效果对比

运行方式（需要 API 密钥环境变量时才测腾讯）：
    pytest tests/integration/infrastructure/test_translation_quality.py -v -s

只测免费翻译：
    pytest tests/integration/infrastructure/test_translation_quality.py -v -s -k "free"

输出为表格，方便人工对比翻译质量。
"""

from __future__ import annotations

import os

import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.text_preprocessing.japanese_romaji import (
    preprocess_romaji,
    romaji_to_hiragana,
)
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.translation.argos_translator import ArgosTranslator
from vrctranslate.infrastructure.translation.google_free_translator import (
    GoogleFreeTranslator,
)
from vrctranslate.infrastructure.translation.tencent_translator import (
    TencentTranslator,
)
from vrctranslate.infrastructure.paths import discover_app_paths
from vrctranslate.infrastructure.translation.argos_model_manager import ArgosModelManager

# ── 测试句 ──────────────────────────────────────────

ZH_SENTENCES = [
    ("zh-01", "你好，很高兴认识你"),
    ("zh-02", "今天天气真好"),
    ("zh-03", "请问最近的电车站怎么走"),
    ("zh-04", "非常感谢您的帮助"),
    ("zh-05", "请稍等一下"),
]

JA_SENTENCES = [
    ("ja-01", "こんにちは、お会いできて嬉しいです"),
    ("ja-02", "今日はいい天気ですね"),
    ("ja-03", "すみません、ちょっと通してください"),
    ("ja-04", "ありがとうございます"),
    ("ja-05", "お疲れ様でした"),
]

ROMajI_SENTENCES = [
    # 期望值按发音转写，空格保留（翻译服务可正确理解）
    ("romaji-01", "konnichiwa", "こんにちわ"),
    ("romaji-02", "ohayou gozaimasu", "おはよう ございます"),
    ("romaji-03", "arigatou", "ありがとう"),
    ("romaji-04", "sumimasen", "すみません"),
    ("romaji-05", "yoroshiku onegaishimasu", "よろしく おねがいします"),
    ("romaji-06", "daijoubu desu ka", "だいじょうぶ です か"),
    ("romaji-07", "sugoi sugoi", "すごい すごい"),
    ("romaji-08", "gomen nasai", "ごめん なさい"),
]

# ── helpers ──────────────────────────────────────────

def _make_profile(provider: str) -> TranslationProfile:
    if provider == "tencent":
        return TranslationProfile(
            provider="tencent",
            timeout_seconds=15,
            api_key=os.environ.get("TENCENT_SECRET_ID", ""),
            model=os.environ.get("TENCENT_SECRET_KEY", ""),
        )
    if provider == "argos":
        return TranslationProfile(provider="argos", timeout_seconds=30)
    return TranslationProfile(provider=provider, timeout_seconds=15)


def _try_translate(
    translator,
    request: TranslationRequest,
    profile: TranslationProfile,
) -> str:
    try:
        result = translator.translate(request, profile)
        return result.translated
    except Exception as exc:
        return f"ERR: {exc}"


def _print_header(title: str) -> None:
    print()
    print("=" * 100)
    print(f"  {title}")
    print("=" * 100)


def _print_subheader(title: str) -> None:
    print()
    print(f"--- {title} ---")


# ── Google Free ──────────────────────────────────────

def test_zh_to_ja_google_free() -> None:
    """中文 → 日语 (Google 免费)"""
    translator = GoogleFreeTranslator()
    profile = _make_profile("google_free")
    _print_header("中文 → 日语 [Google 免费]")
    print(f"{'ID':<8} {'原文':<28} {'译文'}")
    print("-" * 80)
    for sid, text in ZH_SENTENCES:
        req = TranslationRequest(sid, text, "zh-CN", "ja", "self")
        translated = _try_translate(translator, req, profile)
        print(f"{sid:<8} {text:<28} {translated}")


def test_ja_to_zh_google_free() -> None:
    """日语 → 中文 (Google 免费)"""
    translator = GoogleFreeTranslator()
    profile = _make_profile("google_free")
    _print_header("日语 → 中文 [Google 免费]")
    print(f"{'ID':<8} {'原文':<28} {'译文'}")
    print("-" * 80)
    for sid, text in JA_SENTENCES:
        req = TranslationRequest(sid, text, "ja", "zh-CN", "self")
        translated = _try_translate(translator, req, profile)
        print(f"{sid:<8} {text:<28} {translated}")


def test_romaji_to_zh_google_free() -> None:
    """罗马音 → (转假名) → 中文 (Google 免费)"""
    translator = GoogleFreeTranslator()
    profile = _make_profile("google_free")
    _print_header("罗马音 → 假名 → 中文 [Google 免费]")
    print(f"{'ID':<10} {'罗马音':<26} {'假名(预处理)':<20} {'译文'}")
    print("-" * 90)
    for sid, romaji, expected_kana in ROMajI_SENTENCES:
        kana_text, _ = preprocess_romaji(romaji, "ja", True)
        req = TranslationRequest(sid, kana_text, "ja", "zh-CN", "self")
        translated = _try_translate(translator, req, profile)
        print(f"{sid:<10} {romaji:<26} {kana_text:<20} {translated}")


def test_romaji_conversion_accuracy() -> None:
    """罗马音转假名 准确性检查"""
    _print_header("罗马音 → 假名 转换准确性")
    print(f"{'ID':<10} {'罗马音':<26} {'转换结果':<20} {'期望':<20} {'匹配'}")
    print("-" * 90)
    all_match = True
    for sid, romaji, expected in ROMajI_SENTENCES:
        converted = romaji_to_hiragana(romaji)
        match = "✓" if converted == expected else "✗"
        if converted != expected:
            all_match = False
        print(f"{sid:<10} {romaji:<26} {converted:<20} {expected:<20} {match}")
    assert all_match, "部分罗马音转换与期望不符"


# ── Tencent ──────────────────────────────────────────

@pytest.mark.skipif(
    not (os.environ.get("TENCENT_SECRET_ID") and os.environ.get("TENCENT_SECRET_KEY")),
    reason="未设置 TENCENT_SECRET_ID / TENCENT_SECRET_KEY 环境变量",
)
def test_zh_to_ja_tencent() -> None:
    """中文 → 日语 (腾讯)"""
    translator = TencentTranslator()
    profile = _make_profile("tencent")
    _print_header("中文 → 日语 [腾讯 TMT]")
    print(f"{'ID':<8} {'原文':<28} {'译文'}")
    print("-" * 80)
    for sid, text in ZH_SENTENCES:
        req = TranslationRequest(sid, text, "zh-CN", "ja", "self")
        translated = _try_translate(translator, req, profile)
        print(f"{sid:<8} {text:<28} {translated}")


@pytest.mark.skipif(
    not (os.environ.get("TENCENT_SECRET_ID") and os.environ.get("TENCENT_SECRET_KEY")),
    reason="未设置 TENCENT_SECRET_ID / TENCENT_SECRET_KEY 环境变量",
)
def test_ja_to_zh_tencent() -> None:
    """日语 → 中文 (腾讯)"""
    translator = TencentTranslator()
    profile = _make_profile("tencent")
    _print_header("日语 → 中文 [腾讯 TMT]")
    print(f"{'ID':<8} {'原文':<28} {'译文'}")
    print("-" * 80)
    for sid, text in JA_SENTENCES:
        req = TranslationRequest(sid, text, "ja", "zh-CN", "self")
        translated = _try_translate(translator, req, profile)
        print(f"{sid:<8} {text:<28} {translated}")


# ── Argos ────────────────────────────────────────────

@pytest.mark.argos
def test_zh_to_ja_argos() -> None:
    """中文 → 日语 (Argos 离线)"""
    manager = ArgosModelManager(discover_app_paths())
    if not manager.component_available:
        pytest.skip("Argos 组件未安装")
    installed = {
        (m.source_language, m.target_language)
        for m in manager.installed_models()
    }
    if ("zh", "en") not in installed or ("en", "ja") not in installed:
        pytest.skip("Argos zh→en 或 en→ja 模型未安装（需要中转）")
    translator = ArgosTranslator(manager)
    profile = _make_profile("argos")
    _print_header("中文 → 日语 [Argos 中转]")
    print(f"{'ID':<8} {'原文':<28} {'译文'}")
    print("-" * 80)
    for sid, text in ZH_SENTENCES:
        req = TranslationRequest(sid, text, "zh-CN", "ja", "self")
        translated = _try_translate(translator, req, profile)
        print(f"{sid:<8} {text:<28} {translated}")


@pytest.mark.argos
def test_ja_to_zh_argos() -> None:
    """日语 → 中文 (Argos 离线)"""
    manager = ArgosModelManager(discover_app_paths())
    if not manager.component_available:
        pytest.skip("Argos 组件未安装")
    installed = {
        (m.source_language, m.target_language)
        for m in manager.installed_models()
    }
    if ("ja", "en") not in installed or ("en", "zh") not in installed:
        pytest.skip("Argos ja→en 或 en→zh 模型未安装（需要中转）")
    translator = ArgosTranslator(manager)
    profile = _make_profile("argos")
    _print_header("日语 → 中文 [Argos 中转]")
    print(f"{'ID':<8} {'原文':<28} {'译文'}")
    print("-" * 80)
    for sid, text in JA_SENTENCES:
        req = TranslationRequest(sid, text, "ja", "zh-CN", "self")
        translated = _try_translate(translator, req, profile)
        print(f"{sid:<8} {text:<28} {translated}")


# ── 横向对比 ─────────────────────────────────────────

def test_summary_comparison() -> None:
    """横向对比：所有可用服务对同一句子的翻译"""
    _print_header("横向对比：中文→日语")
    text = "你好，今天天气怎么样？"
    print(f"原文: {text}")
    print()
    print(f"{'服务':<20} {'译文'}")
    print("-" * 80)

    # Google Free
    try:
        r = GoogleFreeTranslator().translate(
            TranslationRequest("x", text, "zh-CN", "ja", "self"),
            _make_profile("google_free"),
        )
        print(f"{'Google 免费':<20} {r.translated}")
    except Exception as e:
        print(f"{'Google 免费':<20} ERR: {e}")

    # Tencent
    if os.environ.get("TENCENT_SECRET_ID"):
        try:
            r = TencentTranslator().translate(
                TranslationRequest("x", text, "zh-CN", "ja", "self"),
                _make_profile("tencent"),
            )
            print(f"{'腾讯 TMT':<20} {r.translated}")
        except Exception as e:
            print(f"{'腾讯 TMT':<20} ERR: {e}")

    # Argos
    manager = ArgosModelManager(discover_app_paths())
    if manager.component_available:
        installed = {(m.source_language, m.target_language) for m in manager.installed_models()}
        if ("zh", "en") in installed and ("en", "ja") in installed:
            try:
                r = ArgosTranslator(manager).translate(
                    TranslationRequest("x", text, "zh-CN", "ja", "self"),
                    _make_profile("argos"),
                )
                print(f"{'Argos 中转':<20} {r.translated}")
            except Exception as e:
                print(f"{'Argos 中转':<20} ERR: {e}")

    # 罗马音 → 假名 → 中文 对比
    _print_header("横向对比：罗马音 → 中文")
    romaji = "konnichiwa"
    kana_text, _ = preprocess_romaji(romaji, "ja", True)
    print(f"罗马音: {romaji} → 假名: {kana_text}")
    print()
    print(f"{'服务':<20} {'译文'}")
    print("-" * 80)

    try:
        r = GoogleFreeTranslator().translate(
            TranslationRequest("x", kana_text, "ja", "zh-CN", "self"),
            _make_profile("google_free"),
        )
        print(f"{'Google 免费':<20} {r.translated}")
    except Exception as e:
        print(f"{'Google 免费':<20} ERR: {e}")

    if os.environ.get("TENCENT_SECRET_ID"):
        try:
            r = TencentTranslator().translate(
                TranslationRequest("x", kana_text, "ja", "zh-CN", "self"),
                _make_profile("tencent"),
            )
            print(f"{'腾讯 TMT':<20} {r.translated}")
        except Exception as e:
            print(f"{'腾讯 TMT':<20} ERR: {e}")
