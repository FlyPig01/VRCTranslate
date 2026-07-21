from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BENCH_ROOT = ROOT / "benchmarks"
CACHE_ROOT = BENCH_ROOT / "cache"
DATASETS_ROOT = BENCH_ROOT / "datasets"
MODELS_ROOT = BENCH_ROOT / "models"
RESULTS_ROOT = BENCH_ROOT / "results"
CHARTS_ROOT = BENCH_ROOT / "charts"
TOOLS_ROOT = BENCH_ROOT / "tools"
TMP_ROOT = BENCH_ROOT / "tmp"

FLORES_ROOT = DATASETS_ROOT / "flores200_dataset"
FLEURS_ROOT = DATASETS_ROOT / "fleurs"

TRANSLATION_SAMPLE_COUNT = 30
OCR_SAMPLE_COUNT = 5
ASR_SAMPLE_COUNT = 30
END_TO_END_SAMPLE_COUNT = 10
RANDOM_SEED = 20260721


@dataclass(frozen=True, slots=True)
class LanguageBenchmarkSpec:
    code: str
    display_name: str
    flores_code: str
    ocr_package: str
    font_candidates: tuple[str, ...]
    fleurs_code: str | None = None
    speech_code: str | None = None
    cjk_metric: bool = False


LANGUAGES: tuple[LanguageBenchmarkSpec, ...] = (
    LanguageBenchmarkSpec(
        "zh-CN",
        "简体中文",
        "zho_Hans",
        "zh-CN",
        ("msyh.ttc", "simhei.ttf"),
        "cmn_hans_cn",
        "zh",
        True,
    ),
    LanguageBenchmarkSpec(
        "zh-TW",
        "繁體中文",
        "zho_Hant",
        "zh-CN",
        ("msjh.ttc", "msyh.ttc"),
        cjk_metric=True,
    ),
    LanguageBenchmarkSpec(
        "en",
        "English",
        "eng_Latn",
        "en",
        ("segoeui.ttf", "arial.ttf"),
        "en_us",
        "en",
    ),
    LanguageBenchmarkSpec(
        "ja",
        "日本語",
        "jpn_Jpan",
        "ja",
        ("YuGothM.ttc", "meiryo.ttc"),
        "ja_jp",
        "ja",
        True,
    ),
    LanguageBenchmarkSpec(
        "ko",
        "한국어",
        "kor_Hang",
        "ko",
        ("malgun.ttf",),
        "ko_kr",
        "ko",
        True,
    ),
    LanguageBenchmarkSpec(
        "fr",
        "Français",
        "fra_Latn",
        "latin",
        ("segoeui.ttf", "arial.ttf"),
        "fr_fr",
        "fr",
    ),
    LanguageBenchmarkSpec(
        "de",
        "Deutsch",
        "deu_Latn",
        "latin",
        ("segoeui.ttf", "arial.ttf"),
        "de_de",
        "de",
    ),
    LanguageBenchmarkSpec(
        "es",
        "Español",
        "spa_Latn",
        ("latin"),
        ("segoeui.ttf", "arial.ttf"),
        "es_419",
        "es",
    ),
    LanguageBenchmarkSpec(
        "ru",
        "Русский",
        "rus_Cyrl",
        "cyrillic",
        ("segoeui.ttf", "arial.ttf"),
        "ru_ru",
        "ru",
    ),
)

LANGUAGE_BY_CODE = {item.code: item for item in LANGUAGES}
SPOKEN_LANGUAGES = tuple(item for item in LANGUAGES if item.fleurs_code)
SENSEVOICE_LANGUAGES = tuple(
    item for item in SPOKEN_LANGUAGES if item.code in {"zh-CN", "en", "ja", "ko"}
)


def ensure_directories() -> None:
    for path in (
        CACHE_ROOT,
        DATASETS_ROOT,
        MODELS_ROOT,
        RESULTS_ROOT,
        CHARTS_ROOT,
        TOOLS_ROOT,
        TMP_ROOT,
    ):
        path.mkdir(parents=True, exist_ok=True)
