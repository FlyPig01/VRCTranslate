"""手动裁判用：中日互译 + 罗马音转假名 翻译效果对比

运行方式（默认不发起腾讯真实请求）：
    pytest tests/integration/infrastructure/test_translation_quality.py -v -s

只测免费翻译：
    pytest tests/integration/infrastructure/test_translation_quality.py -v -s -k "free"

使用软件当前配置中的腾讯档案测试罗马音质量（不会输出凭据）：
    $env:VRC_TRANSLATE_TEST_TENCENT_CONFIG="1"
    pytest tests/integration/infrastructure/test_translation_quality.py -v -s -k "vrchat_romaji_to_zh_tencent_quality"
    Remove-Item Env:VRC_TRANSLATE_TEST_TENCENT_CONFIG

输出为表格，方便人工对比翻译质量。
"""

from __future__ import annotations

import os
import time

import pytest

from vrctranslate.application.dto import TranslationProfile
from vrctranslate.application.text_preprocessing.japanese_romaji import (
    preprocess_romaji,
)
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.settings.json_repository import JsonSettingsRepository
from vrctranslate.infrastructure.translation.google_free_translator import (
    GoogleFreeTranslator,
)
from vrctranslate.infrastructure.translation.tencent_translator import (
    TencentTranslator,
)
from vrctranslate.infrastructure.text.wanakana_converter import (
    WanaKanaRomajiConverter,
)

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
    ("romaji-01", "konnichiwa", "こんにちは"),
    ("romaji-02", "ohayou gozaimasu", "おはようございます"),
    ("romaji-03", "arigatou", "ありがとう"),
    ("romaji-04", "sumimasen", "すみません"),
    ("romaji-05", "yoroshiku onegaishimasu", "よろしくおねがいします"),
    ("romaji-06", "daijoubu desu ka", "だいじょうぶですか"),
    ("romaji-07", "sugoi sugoi", "すごいすごい"),
    ("romaji-08", "gomen nasai", "ごめんなさい"),
]

VRCHAT_ROMAJI_SENTENCES = [
    (
        "vrchat-romaji-01",
        "こんにちは、元気ですか？",
        "Konnichiwa, genki desu ka?",
        "你好，最近好吗？",
    ),
    (
        "vrchat-romaji-02",
        "今日の天気は晴れです。",
        "Kyou no tenki wa hare desu.",
        "今天的天气是晴天。",
    ),
    (
        "vrchat-romaji-03",
        "コーヒーを二杯ください。",
        "Koohii o nihai kudasai.",
        "请给我两杯咖啡。",
    ),
    (
        "vrchat-romaji-04",
        "ご飯を食べましたか？",
        "Gohan o tabemashita ka?",
        "你吃饭了吗？",
    ),
    (
        "vrchat-romaji-05",
        "私は明日、東京に行きます。",
        "Watashi wa ashita, Toukyou ni ikimasu.",
        "我明天去东京。",
    ),
    (
        "vrchat-romaji-06",
        "このワールドはとても広いですね。",
        "Kono waarudo wa totemo hiroi desu ne.",
        "这个世界真的很广阔呢。",
    ),
    (
        "vrchat-romaji-07",
        "音量を小さくしてくれませんか？",
        "Onryou o chiisaku shite kuremasen ka?",
        "能把音量调小一点吗？",
    ),
    (
        "vrchat-romaji-08",
        "アバターを変更する方法を教えてください。",
        "Abataa o henkou suru houhou o oshiete kudasai.",
        "请告诉我更换模型的方法。",
    ),
    (
        "vrchat-romaji-09",
        "昨日のイベントは楽しかったです。",
        "Kinou no ibento wa tanoshikatta desu.",
        "昨天的活动很开心。",
    ),
    (
        "vrchat-romaji-10",
        "次はどこに行きますか？",
        "Tsugi wa doko ni ikimasu ka?",
        "接下来去哪里呢？",
    ),
]

ROMAJI_CONVERTER = WanaKanaRomajiConverter()
_TENCENT_CONFIG_TEST = os.environ.get("VRC_TRANSLATE_TEST_TENCENT_CONFIG") == "1"


def _tencent_test_enabled() -> bool:
    return _TENCENT_CONFIG_TEST or bool(
        os.environ.get("TENCENT_SECRET_ID")
        and os.environ.get("TENCENT_SECRET_KEY")
    )

# ── helpers ──────────────────────────────────────────

def _make_profile(provider: str) -> TranslationProfile:
    if provider == "tencent":
        if _TENCENT_CONFIG_TEST:
            profiles = [
                profile
                for profile in JsonSettingsRepository().load().translation.profiles
                if profile.provider == "tencent"
            ]
            if not profiles:
                pytest.skip("当前软件配置中没有腾讯云翻译档案")
            if not profiles[0].api_key.strip() or not profiles[0].model.strip():
                pytest.skip("当前腾讯云翻译档案的凭据不完整")
            return profiles[0]
        return TranslationProfile(
            provider="tencent",
            timeout_seconds=15,
            api_key=os.environ.get("TENCENT_SECRET_ID", ""),
            model=os.environ.get("TENCENT_SECRET_KEY", ""),
        )
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
        kana_text = preprocess_romaji(
            romaji, "ja", "force", ROMAJI_CONVERTER
        ).text
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
        converted = preprocess_romaji(
            romaji, "ja", "force", ROMAJI_CONVERTER
        ).text
        match = "✓" if converted == expected else "✗"
        if converted != expected:
            all_match = False
        print(f"{sid:<10} {romaji:<26} {converted:<20} {expected:<20} {match}")
    assert all_match, "部分罗马音转换与期望不符"


def test_vrchat_romaji_to_zh_google_free_quality() -> None:
    """用户提供的 VRChat 语句：自动转换后与参考中文人工对照。"""
    translator = GoogleFreeTranslator()
    profile = _make_profile("google_free")
    _print_header("VRChat 罗马音 → 假名 → 中文 [Google 免费]")
    for sid, original, romaji, expected_zh in VRCHAT_ROMAJI_SENTENCES:
        kana_text = preprocess_romaji(
            romaji,
            "ja",
            "auto",
            ROMAJI_CONVERTER,
        ).text
        translated = _try_translate(
            translator,
            TranslationRequest(sid, kana_text, "ja", "zh-CN", "self"),
            profile,
        )
        print(f"{sid}: {original}")
        print(f"  罗马音: {romaji}")
        print(f"  预处理: {kana_text}")
        print(f"  参考译文: {expected_zh}")
        print(f"  实际译文: {translated}")


# ── Tencent ──────────────────────────────────────────

@pytest.mark.skipif(
    not _tencent_test_enabled(),
    reason="未显式启用腾讯配置测试，也未设置腾讯测试环境变量",
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
        time.sleep(0.26)


@pytest.mark.skipif(
    not _tencent_test_enabled(),
    reason="未显式启用腾讯配置测试，也未设置腾讯测试环境变量",
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
        time.sleep(0.26)


@pytest.mark.skipif(
    not _tencent_test_enabled(),
    reason="未显式启用腾讯配置测试，也未设置腾讯测试环境变量",
)
def test_vrchat_romaji_to_zh_tencent_quality() -> None:
    """使用当前腾讯档案测试罗马音链路，绝不输出服务凭据。"""
    translator = TencentTranslator()
    profile = _make_profile("tencent")
    _print_header("VRChat 罗马音 → 日文预处理 → 中文 [腾讯云 TMT]")
    for sid, original, romaji, expected_zh in VRCHAT_ROMAJI_SENTENCES:
        kana_text = preprocess_romaji(
            romaji,
            "ja",
            "auto",
            ROMAJI_CONVERTER,
        ).text
        translated = _try_translate(
            translator,
            TranslationRequest(sid, kana_text, "ja", "zh-CN", "self"),
            profile,
        )
        print(f"{sid}: {original}")
        print(f"  罗马音: {romaji}")
        print(f"  预处理: {kana_text}")
        print(f"  参考译文: {expected_zh}")
        print(f"  腾讯译文: {translated}")
        time.sleep(0.26)


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
    if _tencent_test_enabled():
        try:
            r = TencentTranslator().translate(
                TranslationRequest("x", text, "zh-CN", "ja", "self"),
                _make_profile("tencent"),
            )
            print(f"{'腾讯 TMT':<20} {r.translated}")
        except Exception as e:
            print(f"{'腾讯 TMT':<20} ERR: {e}")

    # 罗马音 → 假名 → 中文 对比
    _print_header("横向对比：罗马音 → 中文")
    romaji = "konnichiwa"
    kana_text = preprocess_romaji(
        romaji, "ja", "force", ROMAJI_CONVERTER
    ).text
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

    if _tencent_test_enabled():
        try:
            r = TencentTranslator().translate(
                TranslationRequest("x", kana_text, "ja", "zh-CN", "self"),
                _make_profile("tencent"),
            )
            print(f"{'腾讯 TMT':<20} {r.translated}")
        except Exception as e:
            print(f"{'腾讯 TMT':<20} ERR: {e}")
