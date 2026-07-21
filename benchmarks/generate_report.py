from __future__ import annotations

import csv
import hashlib
import json
import math
import subprocess
from collections import defaultdict
from datetime import date
from pathlib import Path
from typing import Any, Iterable

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from benchmarks.benchmark_config import (
    BENCH_ROOT,
    CHARTS_ROOT,
    LANGUAGE_BY_CODE,
    LANGUAGES,
    MODELS_ROOT,
    RESULTS_ROOT,
    ROOT,
    ensure_directories,
)
from benchmarks.common import machine_metadata, mean, read_json, write_json
from vrctranslate import __version__


REPORT_PATH = ROOT / "多语言全链路质量与性能测试报告.md"
LANGUAGE_ORDER = [item.code for item in LANGUAGES]
LANGUAGE_LABELS = {item.code: item.display_name for item in LANGUAGES}
# 报告正文和图表均为中文。使用中文标签可避免某些 Matplotlib 版本
# 无法对单个标签正确回退到韩文字体，导致导出的 SVG 缺字。
LANGUAGE_LABELS["ko"] = "韩语"
ENGINE_LABELS = {
    "sensevoice_auto_current": "SenseVoice 自动",
    "sensevoice_forced_candidate": "SenseVoice 指定语言",
    "whisper_cpp_base_q5_1": "Whisper base q5_1",
    "whisper_cpp_small_q5_1": "Whisper small q5_1",
}
PROFILE_LABELS = {
    "tencent:腾讯云翻译:default": "腾讯云翻译",
    "aliyun:阿里通用:general": "阿里通用",
    "aliyun:阿里专业:professional": "阿里专业",
    "google_free:public-endpoint": "Google 免费",
}


def _load_csv(name: str) -> list[dict[str, str]]:
    with (RESULTS_ROOT / name).open("r", encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _setup_plotting() -> None:
    candidates = (
        Path("C:/Windows/Fonts/msyh.ttc"),
        Path("C:/Windows/Fonts/malgun.ttf"),
        Path("C:/Windows/Fonts/segoeui.ttf"),
    )
    for path in candidates:
        if path.is_file():
            try:
                matplotlib.font_manager.fontManager.addfont(str(path))
            except RuntimeError:
                pass
    plt.rcParams.update(
        {
            "font.sans-serif": [
                "Microsoft YaHei",
                "Malgun Gothic",
                "Segoe UI",
                "DejaVu Sans",
            ],
            "axes.unicode_minus": False,
            "svg.fonttype": "none",
            "figure.facecolor": "white",
            "axes.facecolor": "#f8fafc",
            "axes.edgecolor": "#cbd5e1",
            "grid.color": "#dbe4ee",
            "grid.alpha": 0.7,
        }
    )


def _save(name: str) -> None:
    plt.tight_layout()
    plt.savefig(CHARTS_ROOT / name, format="svg", bbox_inches="tight")
    plt.close()


def chart_ocr() -> None:
    summary = {item["language"]: item for item in read_json(RESULTS_ROOT / "ocr_summary.json")}
    cases = _load_csv("ocr_cases.csv")
    typical: dict[str, float] = {}
    small: dict[str, float] = {}
    for language in LANGUAGE_ORDER:
        language_cases = [row for row in cases if row["language"] == language]
        typical[language] = mean(
            float(row["cer"])
            for row in language_cases
            if row["font_size"] in {"24", "32"}
        )
        small[language] = mean(
            float(row["cer"])
            for row in language_cases
            if row["font_size"] == "16"
        )
    x = np.arange(len(LANGUAGE_ORDER))
    width = 0.25
    plt.figure(figsize=(12, 5.4))
    plt.bar(
        x - width,
        [small[item] * 100 for item in LANGUAGE_ORDER],
        width,
        label="16px",
        color="#e58b52",
    )
    plt.bar(
        x,
        [typical[item] * 100 for item in LANGUAGE_ORDER],
        width,
        label="24/32px",
        color="#2386a8",
    )
    plt.bar(
        x + width,
        [float(summary[item]["cer"]) * 100 for item in LANGUAGE_ORDER],
        width,
        label="全部条件",
        color="#86b9c8",
    )
    plt.xticks(x, [LANGUAGE_LABELS[item] for item in LANGUAGE_ORDER], rotation=20)
    plt.ylabel("字符错误率 CER（%）")
    plt.title("OCR质量：小字号是中文与韩文的主要风险")
    plt.grid(axis="y")
    plt.legend()
    _save("ocr_quality.svg")

    fig, axes = plt.subplots(1, 2, figsize=(12, 4.8))
    p50 = [float(summary[item]["latency_p50_ms"]) for item in LANGUAGE_ORDER]
    p95 = [float(summary[item]["latency_p95_ms"]) for item in LANGUAGE_ORDER]
    axes[0].bar(x - 0.18, p50, 0.36, label="P50", color="#2a7f9e")
    axes[0].bar(x + 0.18, p95, 0.36, label="P95", color="#df8452")
    axes[0].set_xticks(x, [LANGUAGE_LABELS[item] for item in LANGUAGE_ORDER], rotation=25)
    axes[0].set_ylabel("延迟（ms）")
    axes[0].set_title("OCR暖启动延迟")
    axes[0].legend()
    axes[0].grid(axis="y")
    delta = [float(summary[item]["resources"]["rss_delta_mib"]) for item in LANGUAGE_ORDER]
    axes[1].bar(x, delta, color="#6a93c8")
    axes[1].set_xticks(x, [LANGUAGE_LABELS[item] for item in LANGUAGE_ORDER], rotation=25)
    axes[1].set_ylabel("进程RSS增量峰值（MiB）")
    axes[1].set_title("OCR运行内存增量")
    axes[1].grid(axis="y")
    _save("ocr_performance.svg")

    stress = read_json(RESULTS_ROOT / "ocr_stress_summary.json")
    conditions = ["baseline", "low_contrast", "gaussian_blur", "tight_crop"]
    matrix = np.array(
        [
            [
                next(
                    float(item["cer"])
                    for item in stress
                    if item["language"] == language and item["condition"] == condition
                )
                * 100
                for condition in conditions
            ]
            for language in LANGUAGE_ORDER
        ]
    )
    plt.figure(figsize=(8.5, 6.3))
    image = plt.imshow(matrix, cmap="YlOrRd", aspect="auto", vmin=0, vmax=max(15, matrix.max()))
    plt.colorbar(image, label="CER（%）")
    plt.xticks(range(len(conditions)), ["基线", "低对比度", "轻微模糊", "紧边界"])
    plt.yticks(range(len(LANGUAGE_ORDER)), [LANGUAGE_LABELS[item] for item in LANGUAGE_ORDER])
    for row in range(matrix.shape[0]):
        for column in range(matrix.shape[1]):
            plt.text(column, row, f"{matrix[row, column]:.1f}", ha="center", va="center", fontsize=9)
    plt.title("OCR压力条件字符错误率")
    _save("ocr_stress.svg")


def chart_asr() -> None:
    summary = read_json(RESULTS_ROOT / "asr_summary.json")
    engines = list(ENGINE_LABELS)
    spoken = [item.code for item in LANGUAGES if item.code != "zh-TW"]
    matrix = np.full((len(engines), len(spoken)), np.nan)
    for row, engine in enumerate(engines):
        for column, language in enumerate(spoken):
            item = next(
                (
                    value
                    for value in summary
                    if value["engine"] == engine and value["language"] == language
                ),
                None,
            )
            if item is not None:
                matrix[row, column] = float(item["primary_error_rate"]) * 100
    masked = np.ma.masked_invalid(matrix)
    plt.figure(figsize=(11.5, 5.2))
    image = plt.imshow(masked, cmap="YlOrRd", aspect="auto", vmin=0, vmax=35)
    plt.colorbar(image, label="CER/WER（%）")
    plt.xticks(range(len(spoken)), [LANGUAGE_LABELS[item] for item in spoken])
    plt.yticks(range(len(engines)), [ENGINE_LABELS[item] for item in engines])
    for row in range(matrix.shape[0]):
        for column in range(matrix.shape[1]):
            if math.isnan(matrix[row, column]):
                plt.text(column, row, "—", ha="center", va="center", color="#64748b")
            else:
                plt.text(column, row, f"{matrix[row, column]:.1f}", ha="center", va="center", fontsize=9)
    plt.title("本地语音识别质量：SenseVoice与whisper.cpp")
    _save("asr_quality.svg")

    aggregates = []
    for engine in engines:
        subset = [item for item in summary if item["engine"] == engine]
        aggregates.append(
            {
                "engine": engine,
                "error": mean(float(item["primary_error_rate"]) for item in subset),
                "p50": mean(float(item["latency_p50_ms"]) for item in subset),
                "rtf": mean(float(item["rtf"]) for item in subset),
                "memory": mean(float(item["resources"]["rss_delta_mib"]) for item in subset),
                "model": mean(float(item["model_and_runtime_mib"]) for item in subset),
            }
        )
    plt.figure(figsize=(9.5, 6.0))
    colors = ("#277da1", "#43aa8b", "#f8961e", "#d65780")
    for item, color in zip(aggregates, colors, strict=True):
        plt.scatter(
            item["p50"],
            item["error"] * 100,
            s=max(90, item["model"] * 2.4),
            color=color,
            alpha=0.82,
            edgecolor="white",
            linewidth=1.5,
            label=ENGINE_LABELS[item["engine"]],
        )
        plt.annotate(
            f"RTF {item['rtf']:.2f}\n{item['model']:.0f} MiB",
            (item["p50"], item["error"] * 100),
            xytext=(8, 7),
            textcoords="offset points",
            fontsize=9,
        )
    plt.xlabel("单句延迟P50（ms）")
    plt.ylabel("平均主错误率（%）")
    plt.title("本地ASR质量、延迟与模型体积（气泡大小）")
    plt.grid()
    plt.legend(loc="upper left")
    _save("asr_performance.svg")

    robustness = read_json(RESULTS_ROOT / "asr_robustness_summary.json")
    selected_pairs = [
        ("sensevoice_forced_candidate", "zh-CN"),
        ("sensevoice_forced_candidate", "en"),
        ("sensevoice_forced_candidate", "ja"),
        ("sensevoice_forced_candidate", "ko"),
        ("whisper_cpp_small_q5_1", "fr"),
        ("whisper_cpp_small_q5_1", "de"),
        ("whisper_cpp_small_q5_1", "es"),
        ("whisper_cpp_small_q5_1", "ru"),
    ]
    conditions = ["clean", "low_volume", "noise_10db", "fast_1_15x"]
    matrix = np.array(
        [
            [
                next(
                    float(item["primary_error_rate"])
                    for item in robustness
                    if item["engine"] == engine
                    and item["language"] == language
                    and item["condition"] == condition
                )
                * 100
                for condition in conditions
            ]
            for engine, language in selected_pairs
        ]
    )
    plt.figure(figsize=(8.8, 6.2))
    image = plt.imshow(matrix, cmap="YlOrRd", aspect="auto", vmin=0, vmax=50)
    plt.colorbar(image, label="CER/WER（%）")
    plt.xticks(range(4), ["干净", "低音量", "10dB噪声", "1.15倍速"])
    plt.yticks(
        range(len(selected_pairs)),
        [f"{LANGUAGE_LABELS[language]} · {ENGINE_LABELS[engine]}" for engine, language in selected_pairs],
    )
    for row in range(matrix.shape[0]):
        for column in range(matrix.shape[1]):
            plt.text(column, row, f"{matrix[row, column]:.1f}", ha="center", va="center", fontsize=8.5)
    plt.title("本地ASR压力条件错误率")
    _save("asr_robustness.svg")


def chart_translation() -> None:
    summary = read_json(RESULTS_ROOT / "translation_summary.json")
    profiles = [
        "tencent:腾讯云翻译:default",
        "aliyun:阿里通用:general",
        "aliyun:阿里专业:professional",
    ]
    languages = [item for item in LANGUAGE_ORDER if item != "zh-CN"]
    fig, axes = plt.subplots(2, 1, figsize=(12, 7.8))
    for axis, reverse in zip(axes, (False, True), strict=True):
        matrix = []
        for profile in profiles:
            row = []
            for language in languages:
                source, target = (
                    ("zh-CN", language) if reverse else (language, "zh-CN")
                )
                item = next(
                    value
                    for value in summary
                    if value["profile_key"] == profile
                    and value["source_language"] == source
                    and value["target_language"] == target
                )
                row.append(float(item["chrf"]))
            matrix.append(row)
        values = np.array(matrix)
        image = axis.imshow(values, cmap="YlGn", aspect="auto", vmin=15, vmax=60)
        axis.set_xticks(range(len(languages)), [LANGUAGE_LABELS[item] for item in languages])
        axis.set_yticks(range(len(profiles)), [PROFILE_LABELS[item] for item in profiles])
        axis.set_title("简中 → 其他语言" if reverse else "其他语言 → 简中")
        for row in range(values.shape[0]):
            for column in range(values.shape[1]):
                axis.text(column, row, f"{values[row, column]:.1f}", ha="center", va="center", fontsize=8.5)
    fig.colorbar(image, ax=axes, label="FLORES chrF++", fraction=0.025, pad=0.03)
    fig.suptitle("真实机器翻译质量矩阵（单参考译文，仅用于同方向比较）", fontsize=14)
    plt.savefig(CHARTS_ROOT / "translation_quality.svg", format="svg", bbox_inches="tight")
    plt.close()

    resources = read_json(RESULTS_ROOT / "translation_resources.json")
    paid = [item for item in resources if item["provider"] != "google_free"]
    labels = [item["profile_name"] for item in paid]
    p50 = []
    p95 = []
    for item in paid:
        subset = [
            value
            for value in summary
            if value["profile_key"] == item["profile_key"] and value["succeeded"]
        ]
        p50.append(mean(float(value["latency_p50_ms"]) for value in subset))
        p95.append(mean(float(value["latency_p95_ms"]) for value in subset))
    x = np.arange(len(labels))
    plt.figure(figsize=(8.4, 4.8))
    plt.bar(x - 0.18, p50, 0.36, label="P50", color="#2a9d8f")
    plt.bar(x + 0.18, p95, 0.36, label="P95", color="#e76f51")
    plt.xticks(x, labels)
    plt.ylabel("请求延迟（ms）")
    plt.title("真实翻译接口延迟（16方向平均）")
    plt.grid(axis="y")
    plt.legend()
    _save("translation_latency.svg")

    domain = read_json(RESULTS_ROOT / "domain_invariant_summary.json")
    plt.figure(figsize=(8.4, 4.8))
    labels = [item["profile_name"] for item in domain]
    values = [float(item["invariant_preservation_rate"]) * 100 for item in domain]
    compatible = [float(item["glossary_compatible_rate"]) * 100 for item in domain]
    x = np.arange(len(labels))
    plt.bar(x - 0.18, values, 0.36, label="不变量保留率", color="#277da1")
    plt.bar(x + 0.18, compatible, 0.36, label="术语占位兼容率", color="#90be6d")
    plt.xticks(x, labels)
    plt.ylim(0, 105)
    plt.ylabel("比例（%）")
    plt.title("VRChat术语、用户名、URL和数字保护")
    plt.grid(axis="y")
    plt.legend()
    _save("domain_preservation.svg")


def chart_end_to_end() -> None:
    summary = read_json(RESULTS_ROOT / "end_to_end_summary.json")
    ocr = {item["language"]: item for item in summary if item["pipeline"] == "ocr_translation"}
    asr = {item["language"]: item for item in summary if item["pipeline"] == "asr_translation"}
    languages = [item.code for item in LANGUAGES if item.code != "zh-TW"]
    x = np.arange(len(languages))
    plt.figure(figsize=(11.5, 5.2))
    plt.bar(
        x - 0.18,
        [float(ocr[item]["total_latency_p50_ms"]) for item in languages],
        0.36,
        label="OCR→翻译",
        color="#2386a8",
    )
    plt.bar(
        x + 0.18,
        [float(asr[item]["total_latency_p50_ms"]) for item in languages],
        0.36,
        label="ASR→翻译",
        color="#d77a48",
    )
    plt.xticks(x, [LANGUAGE_LABELS[item] for item in languages])
    plt.ylabel("端到端P50（ms）")
    plt.title("最佳可用组合的端到端延迟")
    plt.grid(axis="y")
    plt.legend()
    _save("end_to_end_latency.svg")


def _cell(value: Any) -> str:
    text = str(value).replace("\n", " ").replace("|", "\\|")
    return text


def _table(headers: list[str], rows: Iterable[Iterable[Any]]) -> str:
    output = [
        "| " + " | ".join(headers) + " |",
        "|" + "|".join("---" for _ in headers) + "|",
    ]
    output.extend("| " + " | ".join(_cell(item) for item in row) + " |" for row in rows)
    return "\n".join(output)


def _pct(value: Any, digits: int = 1) -> str:
    return f"{float(value) * 100:.{digits}f}%"


def _ms(value: Any) -> str:
    return f"{float(value):.0f} ms"


def _best_translation_rows(summary: list[dict[str, Any]]) -> list[list[Any]]:
    rows: list[list[Any]] = []
    for source in LANGUAGE_ORDER:
        if source == "zh-CN":
            continue
        for actual_source, target in ((source, "zh-CN"), ("zh-CN", source)):
            candidates = [
                item
                for item in summary
                if item["source_language"] == actual_source
                and item["target_language"] == target
                and item["chrf"] is not None
            ]
            best = max(candidates, key=lambda item: float(item["chrf"]))
            rows.append(
                [
                    f"{LANGUAGE_LABELS[actual_source]} → {LANGUAGE_LABELS[target]}",
                    best["profile_name"],
                    f"{float(best['chrf']):.2f}",
                    _ms(best["latency_p50_ms"]),
                    _ms(best["latency_p95_ms"]),
                ]
            )
    return rows


def _worst_translation_examples(
    cases: list[dict[str, str]],
    routes: dict[str, str],
) -> list[list[str]]:
    output: list[list[str]] = []
    for language in ("en", "ja", "ko", "fr", "de", "es", "ru"):
        candidates = [
            row
            for row in cases
            if row["source_language"] == language
            and row["target_language"] == "zh-CN"
            and row["profile_key"] == routes[language]
            and row["status"] == "ok"
        ]
        row = min(candidates, key=lambda item: float(item["sentence_chrf"]))
        output.append(
            [
                LANGUAGE_LABELS[language],
                row["profile_name"],
                f"{float(row['sentence_chrf']):.1f}",
                row["reference"][:72],
                row["hypothesis"][:72],
            ]
        )
    return output


def _system_metadata() -> dict[str, Any]:
    metadata = machine_metadata()
    try:
        cpu = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                "(Get-CimInstance Win32_Processor | Select-Object -First 1).Name",
            ],
            capture_output=True,
            text=True,
            timeout=10,
            check=True,
        ).stdout.strip()
        if cpu:
            metadata["processor"] = cpu
    except (OSError, subprocess.SubprocessError):
        pass
    return metadata


def generate_report() -> None:
    ocr = read_json(RESULTS_ROOT / "ocr_summary.json")
    ocr_cases = _load_csv("ocr_cases.csv")
    ocr_stress = read_json(RESULTS_ROOT / "ocr_stress_summary.json")
    asr = read_json(RESULTS_ROOT / "asr_summary.json")
    asr_robustness = read_json(RESULTS_ROOT / "asr_robustness_summary.json")
    translation = read_json(RESULTS_ROOT / "translation_summary.json")
    translation_cases = _load_csv("translation_cases.csv")
    translation_resources = read_json(RESULTS_ROOT / "translation_resources.json")
    availability = read_json(RESULTS_ROOT / "translation_availability.json")
    domain = read_json(RESULTS_ROOT / "domain_invariant_summary.json")
    end_to_end = read_json(RESULTS_ROOT / "end_to_end_summary.json")
    routes = read_json(RESULTS_ROOT / "selected_routes.json")
    system = _system_metadata()

    typical_cer: dict[str, float] = {}
    size16_cer: dict[str, float] = {}
    for language in LANGUAGE_ORDER:
        subset = [row for row in ocr_cases if row["language"] == language]
        typical_cer[language] = mean(
            float(row["cer"]) for row in subset if row["font_size"] in {"24", "32"}
        )
        size16_cer[language] = mean(
            float(row["cer"]) for row in subset if row["font_size"] == "16"
        )

    translation_aggregate = []
    for resource in translation_resources:
        subset = [
            item
            for item in translation
            if item["profile_key"] == resource["profile_key"] and item["chrf"] is not None
        ]
        translation_aggregate.append(
            [
                resource["profile_name"],
                resource["requests"],
                resource["source_characters"],
                _pct(mean(float(item["success_rate"]) for item in subset)),
                f"{mean(float(item['chrf']) for item in subset):.2f}" if subset else "—",
                _ms(mean(float(item["latency_p50_ms"]) for item in subset)) if subset else "—",
                _ms(mean(float(item["latency_p95_ms"]) for item in subset)) if subset else "—",
            ]
        )

    ocr_table = []
    for item in ocr:
        language = item["language"]
        ocr_table.append(
            [
                item["language_name"],
                item["ocr_package"],
                _pct(item["cer"]),
                _pct(typical_cer[language]),
                _pct(size16_cer[language]),
                _pct(item["exact_rate"]),
                _ms(item["latency_p50_ms"]),
                _ms(item["latency_p95_ms"]),
                f"{float(item['resources']['rss_delta_mib']):.0f} MiB",
            ]
        )

    asr_table = []
    for item in asr:
        asr_table.append(
            [
                ENGINE_LABELS[item["engine"]],
                LANGUAGE_LABELS[item["language"]],
                item["primary_metric"].upper(),
                _pct(item["primary_error_rate"]),
                f"{float(item['rtf']):.3f}",
                _ms(item["latency_p50_ms"]),
                _ms(item["latency_p95_ms"]),
                f"{float(item['resources']['rss_delta_mib']):.0f} MiB",
                f"{float(item['model_and_runtime_mib']):.1f} MiB",
            ]
        )

    e2e_table = []
    for item in end_to_end:
        recognition = item.get("ocr_cer", item.get("asr_primary_error_rate", 0))
        engine = item.get("asr_engine", "OCR")
        e2e_table.append(
            [
                "OCR→翻译" if item["pipeline"] == "ocr_translation" else "ASR→翻译",
                LANGUAGE_LABELS[item["language"]],
                ENGINE_LABELS.get(engine, engine),
                PROFILE_LABELS.get(item["profile_key"], item["profile_key"]),
                _pct(recognition),
                f"{float(item['translation_chrf']):.2f}",
                _ms(item["total_latency_p50_ms"]),
                _ms(item["total_latency_p95_ms"]),
            ]
        )

    clean_robustness = {
        (item["engine"], item["language"], item["condition"]): item
        for item in asr_robustness
    }
    asr_recommendations = []
    for language in ("zh-CN", "en", "ja", "ko"):
        item = next(
            value
            for value in asr
            if value["engine"] == "sensevoice_forced_candidate"
            and value["language"] == language
        )
        noise = clean_robustness[("sensevoice_forced_candidate", language, "noise_10db")]
        asr_recommendations.append(
            [
                LANGUAGE_LABELS[language],
                "SenseVoiceSmall INT8",
                _pct(item["primary_error_rate"]),
                _pct(noise["primary_error_rate"]),
                _ms(item["latency_p50_ms"]),
                "推荐",
            ]
        )
    for language in ("fr", "de", "es", "ru"):
        item = next(
            value
            for value in asr
            if value["engine"] == "whisper_cpp_small_q5_1"
            and value["language"] == language
        )
        noise = clean_robustness[("whisper_cpp_small_q5_1", language, "noise_10db")]
        if language == "es":
            decision = "实验性可用；延迟高"
        elif language == "ru":
            decision = "暂缓；噪声下不稳定"
        else:
            decision = "不推荐"
        asr_recommendations.append(
            [
                LANGUAGE_LABELS[language],
                "whisper.cpp small q5_1",
                _pct(item["primary_error_rate"]),
                _pct(noise["primary_error_rate"]),
                _ms(item["latency_p50_ms"]),
                decision,
            ]
        )

    files = sorted(RESULTS_ROOT.glob("*"))
    result_files = "\n".join(
        f"- [{path.name}](benchmarks/results/{path.name})" for path in files
    )
    missing_services = [
        provider
        for provider, value in availability.items()
        if not value.get("configured")
    ]
    paid_result_calls = sum(
        int(item["succeeded"])
        for item in translation
        if item["provider"] in {"tencent", "aliyun"}
    ) + sum(int(item["cases"]) for item in end_to_end)

    report = f"""# VRCTranslate 多语言全链路质量与性能测试报告

## 1. 结论摘要

- 测试日期：{date.today().isoformat()}
- 软件版本：v{__version__}
- 测试范围：九语言OCR、中文中心16个翻译方向、八种口语的本地ASR候选、OCR/ASR端到端组合。
- OCR共执行 {len(ocr_cases)} 个基础样本和 {len(_load_csv('ocr_stress_cases.csv'))} 个压力样本；所有图片在内存中生成，没有保存截图。
- ASR共执行 {len(_load_csv('asr_cases.csv'))} 个基础识别和 {len(_load_csv('asr_robustness_cases.csv'))} 个压力识别，使用FLEURS真人音频。
- 三个付费翻译档案各完成480/480次标准请求；结果集与端到端共包含约 {paid_result_calls} 次成功付费调用。Google免费端点连续超时后熔断。
- 当前最可靠组合：中英日韩语音继续使用SenseVoice；whisper.cpp不能替换SenseVoice。西班牙语可把Whisper small作为实验性组件，法德俄仍不适合作为默认实时字幕。
- OCR最突出的问题是16px中文/韩文；24/32px时九语言CER均降到3.4%以内。
- VRChat领域测试发现URL和标识符保护不足，必须增加独立于术语库的通用不变量保护。

## 2. 测试环境与边界

{_table(
    ["项目", "值"],
    [
        ["CPU", system.get("processor", "")],
        ["物理/逻辑核心", f"{system['physical_cpu_count']} / {system['logical_cpu_count']}"],
        ["内存", f"{system['memory_total_gib']:.1f} GiB"],
        ["操作系统", system["platform"]],
        ["Python", "3.11.4"],
        ["OCR线程", "ONNX Runtime 2个intra-op线程"],
        ["ASR线程", "4线程、CPU模式"],
    ],
)}

本报告不是所有72个语言对的穷举，而是已批准的产品级矩阵：每种语言与简体中文双向。翻译质量使用FLORES-200的30条对齐句；chrF、BLEU和TER只适合同一方向、同一参考集内比较。合法改写、专名音译差异会得到较低自动分，因此低chrF样本必须人工复核。

未配置且无法真实测试的服务：{', '.join(missing_services) if missing_services else '无'}。没有读取或输出任何密钥。多模态档案未配置，未纳入本轮。

数据来源：FLORES-200平行文本；FLEURS语音数据集，数据卡声明CC-BY-4.0。每种口语只保留30段2～12秒、可与FLORES参考译文对齐的样本；完整语音归档已删除。

## 3. OCR质量与性能

![OCR质量](benchmarks/charts/ocr_quality.svg)

{_table(
    ["语言", "模型包", "全条件CER", "24/32px CER", "16px CER", "整句准确率", "P50", "P95", "RSS增量峰值"],
    ocr_table,
)}

关键判断：

- 日文模型最稳定，24/32px样本为0 CER。
- 中文从16px的{_pct(size16_cer['zh-CN'])}降到24/32px的{_pct(typical_cer['zh-CN'])}。
- 韩文16px CER达到{_pct(size16_cer['ko'])}，但24/32px降到{_pct(typical_cer['ko'])}；问题主要是检测器无法稳定处理小字，不是韩文识别包整体不可用。
- 英法德西24/32px CER为0.1%～1.0%，轻量拉丁模型在典型字幕尺寸下可用。
- 俄文24/32px CER为{_pct(typical_cer['ru'])}，且对轻微模糊更敏感。
- 暖启动P50约0.56～0.92秒，当前250ms周期不应理解为每250ms都能完成一次OCR；必须使用“只保留最新帧”的调度策略。

![OCR性能](benchmarks/charts/ocr_performance.svg)

![OCR压力测试](benchmarks/charts/ocr_stress.svg)

## 4. 本地语音识别

![ASR质量](benchmarks/charts/asr_quality.svg)

{_table(
    ["引擎", "语言", "指标", "错误率", "RTF", "P50", "P95", "RSS增量峰值", "模型+运行库"],
    asr_table,
)}

SenseVoice自动与指定语言的差距很小：指定语言对韩语改善最明显，但四种语言均远优于Whisper base。官方当前提供的是`base q5_1`，不是先前网络表格中的`base q5_0`；实测base模型约56.9MiB，small约181.2MiB。

![ASR性能](benchmarks/charts/asr_performance.svg)

![ASR压力测试](benchmarks/charts/asr_robustness.svg)

{_table(
    ["语言", "推荐引擎", "干净错误率", "10dB噪声错误率", "P50", "结论"],
    asr_recommendations,
)}

Whisper small虽然使法德西俄干净语音WER降到10.8%～18.7%，但P50约2.5秒；法语和德语在10dB噪声下WER超过45%。它能做到RTF<1，但“处理得完”不等于“字幕及时”。

## 5. 真实翻译接口

![翻译质量矩阵](benchmarks/charts/translation_quality.svg)

{_table(
    ["档案", "样本记录", "源字符数", "成功率", "平均chrF", "平均P50", "平均P95"],
    translation_aggregate,
)}

- 腾讯云、阿里通用、阿里专业均480/480成功。
- Google免费端点5条连续请求均发生主端点与备用端点超时，随后475条由熔断器跳过；不能作为默认实时服务。
- 腾讯云在英、日、韩、法、德、西、俄→简中方向的chrF均最高。
- 中文→其他语言没有单一赢家：差距较小时必须结合领域样本和母语审校，不能只按自动分切换付费服务。
- 阿里专业平均P50/P95最快；腾讯P95存在较大长尾。

![翻译延迟](benchmarks/charts/translation_latency.svg)

### 每个方向的本轮最高chrF档案

{_table(["方向", "档案", "chrF", "P50", "P95"], _best_translation_rows(translation))}

### 自动低分样本人工复核入口

下表是所选“源语言→中文”档案中chrF最低的样本。多项译文语义仍然合理，低分主要来自专名音译和参考译文改写，因此不能直接当作严重误译率。

{_table(["语言", "档案", "句级chrF", "参考译文", "实际译文"], _worst_translation_examples(translation_cases, routes['translation']))}

确实发现的明显问题之一：阿里通用把韩语“圣殿骑士团成立背景”错误处理成“理解寺庙、创造秩序”。这说明平均分较低的韩→中方向存在真实语义风险。

## 6. VRChat术语与不变量

![术语与不变量](benchmarks/charts/domain_preservation.svg)

{_table(
    ["档案", "成功率", "VRChat/OSC/用户名/URL/数字保留率", "术语占位兼容率"],
    [
        [item['profile_name'], _pct(item['success_rate']), _pct(item['invariant_preservation_rate']), _pct(item['glossary_compatible_rate'])]
        for item in domain
    ],
)}

阿里接口能稳定保留术语占位符，但仍会把URL中的`world`翻译、替换问号或在`id=42`周围插入空格。腾讯在部分语言中会把`VRChat`拆成`V RC hat`、改变大小写或破坏URL。默认术语库无法覆盖任意URL、用户名和代码，因此需要独立保护层。

## 7. 端到端结果

![端到端延迟](benchmarks/charts/end_to_end_latency.svg)

{_table(
    ["链路", "源语言", "识别器", "翻译档案", "识别错误率", "译文chrF", "总P50", "总P95"],
    e2e_table,
)}

- OCR→翻译P50约0.9～1.4秒，主要时间花在OCR。
- SenseVoice→翻译P50约0.4～0.53秒，适合中英日韩字幕。
- Whisper small→翻译P50约2.9秒，法德西俄实时体验明显落后。
- 法德俄语音端到端低质量首先来自ASR；日/韩语音端到端中，识别错误较低，译文差异更多来自机器翻译与单参考指标。

## 8. 翻译质量提升方案

### P0：先修复确定性问题

1. OCR识别模型与翻译源语言解耦。例如选择“拉丁文字OCR”，翻译源语言仍可单独选择法/德/西或由支持自动检测的服务判断。
2. 本地SenseVoice真正接收用户选择的语言；保持自动模式为默认，指定语言作为高级准确性选项。不要用Whisper替换中英日韩现有模型。
3. 新增通用不变量保护：在术语替换之前识别并占位保护URL、邮箱、`@用户名`、VRChat用户名、路径、代码片段、数字和OSC地址；翻译后严格恢复并校验。
4. Google免费端点标为“不稳定/无SLA”，连接测试失败时不允许进入默认路由。
5. 修复“阿里专业”档案实际保存为`general`的问题，界面必须显示并持久化真实接口类型。

### P1：OCR实时性与小字

1. 文字高度低于约22px时，对识别输入自适应放大1.5～2倍；识别框坐标再映射回原图。
2. 选区四周增加4～8px识别内边距，韩文紧边界CER由基线0.9%升到13.1%，证明边缘背景十分重要。
3. 对韩文16px优先改进检测模型或放大策略，不要先替换识别字典。
4. 对俄文模糊场景增加轻量锐化/对比度分支，但只在置信度较低时触发，避免每帧双重OCR。
5. OCR调度采用单飞任务和latest-frame-wins；建议有效间隔不短于对应语言暖启动P50，拖动窗口期间暂停结果绘制。
6. 切换语言模型时显式释放旧ONNX Session并观察RSS，当前基准进程增量峰值约0.48～0.88GiB。

### P1：按语言方向给出推荐，而不是宣称服务质量一致

1. 在用户已配置档案中显示方向质量建议；不得未经允许自动调用新的付费服务。
2. 其他语言→简中优先提示腾讯；简中→目标语言根据方向分别推荐，不设单一全局赢家。
3. 对分数接近3分以内的档案优先选择低P95或让用户决定，避免自动指标过拟合单参考译文。
4. 后续配置DeepL后，重点补测简中↔法/德/西，当前缺少密钥，不能提前声称DeepL更好。
5. 将FLORES通用语料与VRChat口语分开评分，新增否定、人称、敬语、俚语、房间名和Avatar术语的母语审校集。

### P2：本地欧洲语言语音

1. `whisper.cpp base q5_1`质量不足，不接入正式软件。
2. `small q5_1`仅把西班牙语列为实验性可选项；法语、德语、俄语继续延期。
3. 若继续研究，优先测试更强模型、VAD切句和初始提示，但必须把P50控制到约1秒；仅降低WER而维持2.5秒延迟不满足实时字幕需求。
4. Whisper作为懒加载独立组件，不能与SenseVoice一起打入基础包。用户选定模型后删除未采用模型和运行依赖。

## 9. 可复现性与限制

- OCR图像由固定系统字体即时生成，没有保留截图；真实VRChat复杂背景仍需用户现场验证。
- FLEURS是清晰真人语音；压力测试加入低音量、10dB白噪声和1.15倍速，但不等同于所有游戏混音环境。
- 自动翻译指标不能替代母语审校。本报告可用于发现相对差异和严重候选，不应被描述成语言学权威排名。
- 在线接口结果会随服务升级、网络和套餐变化，应在每次功能版本发布前复跑小型回归集。
- 性能数字只代表本机 Ryzen 7 7840H、4线程ASR和当前ONNX配置。

FLORES归档SHA-256：`{_sha256(BENCH_ROOT / 'cache' / 'flores200_dataset.tar.gz')}`。

Whisper模型：

- `ggml-base-q5_1.bin`：{(MODELS_ROOT / 'whisper.cpp' / 'ggml-base-q5_1.bin').stat().st_size / 2**20:.1f} MiB，SHA-256 `{_sha256(MODELS_ROOT / 'whisper.cpp' / 'ggml-base-q5_1.bin')}`。
- `ggml-small-q5_1.bin`：{(MODELS_ROOT / 'whisper.cpp' / 'ggml-small-q5_1.bin').stat().st_size / 2**20:.1f} MiB，SHA-256 `{_sha256(MODELS_ROOT / 'whisper.cpp' / 'ggml-small-q5_1.bin')}`。

## 10. 结果文件

{result_files}
"""
    REPORT_PATH.write_text(report, encoding="utf-8")
    write_json(
        RESULTS_ROOT / "benchmark_metadata.json",
        {
            "date": date.today().isoformat(),
            "version": __version__,
            "machine": system,
            "report": str(REPORT_PATH.name),
            "charts": sorted(path.name for path in CHARTS_ROOT.glob("*.svg")),
            "secrets_included": False,
        },
    )


def main() -> int:
    ensure_directories()
    _setup_plotting()
    chart_ocr()
    chart_asr()
    chart_translation()
    chart_end_to_end()
    generate_report()
    print(REPORT_PATH)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
