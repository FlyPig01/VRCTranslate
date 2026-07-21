from __future__ import annotations

import re
import time
from dataclasses import replace
from typing import Any
from uuid import uuid4

from sacrebleu.metrics import BLEU, CHRF, TER

from benchmarks.benchmark_config import (
    LANGUAGE_BY_CODE,
    LANGUAGES,
    RESULTS_ROOT,
    TRANSLATION_SAMPLE_COUNT,
    ensure_directories,
)
from benchmarks.common import (
    ResourceMonitor,
    aligned_sample_indices,
    dataclass_dict,
    flores_lines,
    mean,
    percentile,
    write_csv,
    write_json,
)
from vrctranslate.application.dto import TranslationProfile
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.paths import discover_app_paths
from vrctranslate.infrastructure.settings.json_repository import JsonSettingsRepository
from vrctranslate.infrastructure.translation.aliyun_translator import AliyunTranslator
from vrctranslate.infrastructure.translation.deepl_translator import DeepLTranslator
from vrctranslate.infrastructure.translation.google_cloud_translator import (
    GoogleCloudTranslator,
)
from vrctranslate.infrastructure.translation.google_free_translator import (
    GoogleFreeTranslator,
)
from vrctranslate.infrastructure.translation.openai_compatible import (
    OpenAICompatibleTranslator,
)
from vrctranslate.infrastructure.translation.tencent_translator import (
    TencentTranslator,
)


_PROVIDER_TYPES = (
    "tencent",
    "aliyun",
    "deepl",
    "google_cloud",
    "google_free",
    "openai_compatible",
)
_NUMBER = re.compile(r"\d+(?:[.,]\d+)?")


def _adapter(provider: str) -> object:
    return {
        "tencent": TencentTranslator,
        "aliyun": AliyunTranslator,
        "deepl": DeepLTranslator,
        "google_cloud": GoogleCloudTranslator,
        "google_free": GoogleFreeTranslator,
        "openai_compatible": OpenAICompatibleTranslator,
    }[provider]()


def _profiles() -> tuple[list[tuple[str, TranslationProfile, object]], dict[str, Any]]:
    settings = JsonSettingsRepository(app_paths=discover_app_paths()).load()
    selected: list[tuple[str, TranslationProfile, object]] = []
    availability: dict[str, Any] = {
        provider: {"configured": False, "profiles": []} for provider in _PROVIDER_TYPES
    }
    for profile in settings.translation.profiles:
        provider = profile.provider
        if provider not in availability:
            continue
        complete = True
        if provider in {"tencent", "aliyun"}:
            complete = bool(profile.api_key.strip() and profile.model.strip())
        elif provider in {"deepl", "google_cloud", "openai_compatible"}:
            complete = bool(profile.api_key.strip())
        tested = profile
        stored_mode = str(profile.options.get("aliyun_api", "general"))
        tested_mode = stored_mode
        if provider == "aliyun" and "专业" in profile.name and stored_mode != "professional":
            options = dict(profile.options)
            options["aliyun_api"] = "professional"
            tested = replace(profile, options=options)
            tested_mode = "professional"
        availability[provider]["profiles"].append(
            {
                "name": profile.name,
                "complete": complete,
                "stored_mode": stored_mode if provider == "aliyun" else None,
                "tested_mode": tested_mode if provider == "aliyun" else None,
            }
        )
        if not complete:
            continue
        mode_suffix = tested_mode if provider == "aliyun" else "default"
        key = f"{provider}:{profile.name}:{mode_suffix}"
        selected.append((key, tested, _adapter(provider)))
        availability[provider]["configured"] = True
    google_profile = TranslationProfile(
        id="benchmark-google-free",
        name="Google翻译（免费）",
        provider="google_free",
        base_url="https://translate.googleapis.com/translate_a/single",
        timeout_seconds=15.0,
    )
    selected.append(
        ("google_free:public-endpoint", google_profile, GoogleFreeTranslator())
    )
    availability["google_free"] = {
        "configured": True,
        "profiles": [{"name": google_profile.name, "complete": True}],
        "unofficial_endpoint": True,
    }
    return selected, availability


def _directions() -> list[tuple[str, str]]:
    output: list[tuple[str, str]] = []
    for language in (item.code for item in LANGUAGES if item.code != "zh-CN"):
        output.append((language, "zh-CN"))
        output.append(("zh-CN", language))
    return output


def _minimum_interval(provider: str) -> float:
    if provider == "google_free":
        return 0.45
    if provider == "tencent":
        return 0.23
    return 0.05


def _translate_with_retry(
    adapter: object,
    request: TranslationRequest,
    profile: TranslationProfile,
) -> tuple[str, int]:
    retries = 0
    while True:
        try:
            return adapter.translate(request, profile).translated, retries
        except Exception as exc:
            category = str(getattr(exc, "category", "unexpected"))
            if retries >= 1 or category not in {"network", "service", "quota"}:
                raise
            retries += 1
            time.sleep(1.5)


def _numeric_recall(reference: str, hypothesis: str) -> float | None:
    expected = _NUMBER.findall(reference)
    if not expected:
        return None
    actual = _NUMBER.findall(hypothesis)
    remaining = list(actual)
    hits = 0
    for token in expected:
        if token in remaining:
            remaining.remove(token)
            hits += 1
    return hits / len(expected)


def main() -> int:
    ensure_directories()
    profiles, availability = _profiles()
    source_lines = {item.code: flores_lines(item.code) for item in LANGUAGES}
    population = min(len(lines) for lines in source_lines.values())
    indices = aligned_sample_indices(TRANSLATION_SAMPLE_COUNT, population)
    chrf = CHRF(word_order=2)
    bleu = BLEU(tokenize="intl", effective_order=True)
    ter = TER(asian_support=True)
    all_cases: list[dict[str, Any]] = []
    all_summaries: list[dict[str, Any]] = []
    profile_resources: list[dict[str, Any]] = []
    for profile_key, profile, adapter in profiles:
        print(f"[translation] profile={profile.name} provider={profile.provider}", flush=True)
        profile_cases: list[dict[str, Any]] = []
        consecutive_failures = 0
        circuit_open = False
        with ResourceMonitor() as monitor:
            for source, target in _directions():
                direction_cases: list[dict[str, Any]] = []
                for index in indices:
                    source_text = source_lines[source][index]
                    reference = source_lines[target][index]
                    row: dict[str, Any] = {
                        "profile_key": profile_key,
                        "profile_name": profile.name,
                        "provider": profile.provider,
                        "source_language": source,
                        "target_language": target,
                        "sample_index": index,
                        "source": source_text,
                        "reference": reference,
                        "source_characters": len(source_text),
                    }
                    if circuit_open:
                        row.update(
                            status="skipped_circuit_open",
                            hypothesis="",
                            latency_ms=0.0,
                            retries=0,
                            error_category="service",
                        )
                    else:
                        request = TranslationRequest(
                            uuid4().hex,
                            source_text,
                            source,
                            target,
                            "self",
                        )
                        started = time.perf_counter()
                        try:
                            hypothesis, retries = _translate_with_retry(
                                adapter,
                                request,
                                profile,
                            )
                            latency_ms = (time.perf_counter() - started) * 1000
                            sentence_chrf = chrf.sentence_score(
                                hypothesis,
                                [reference],
                            ).score
                            row.update(
                                status="ok",
                                hypothesis=hypothesis,
                                latency_ms=latency_ms,
                                retries=retries,
                                error_category="",
                                sentence_chrf=sentence_chrf,
                                numeric_recall=_numeric_recall(reference, hypothesis),
                            )
                            consecutive_failures = 0
                        except Exception as exc:
                            row.update(
                                status="failed",
                                hypothesis="",
                                latency_ms=(time.perf_counter() - started) * 1000,
                                retries=1,
                                error_category=str(
                                    getattr(exc, "category", type(exc).__name__)
                                ),
                                error_message=str(
                                    getattr(exc, "user_message", type(exc).__name__)
                                )[:240],
                            )
                            consecutive_failures += 1
                            if consecutive_failures >= 5:
                                circuit_open = True
                        remaining = _minimum_interval(profile.provider) - (
                            time.perf_counter() - started
                        )
                        if remaining > 0:
                            time.sleep(remaining)
                    all_cases.append(row)
                    profile_cases.append(row)
                    direction_cases.append(row)
                successful = [row for row in direction_cases if row["status"] == "ok"]
                hypotheses = [str(row["hypothesis"]) for row in successful]
                references = [str(row["reference"]) for row in successful]
                numeric = [
                    float(row["numeric_recall"])
                    for row in successful
                    if row.get("numeric_recall") is not None
                ]
                summary = {
                    "profile_key": profile_key,
                    "profile_name": profile.name,
                    "provider": profile.provider,
                    "source_language": source,
                    "source_name": LANGUAGE_BY_CODE[source].display_name,
                    "target_language": target,
                    "target_name": LANGUAGE_BY_CODE[target].display_name,
                    "requested": len(direction_cases),
                    "succeeded": len(successful),
                    "success_rate": len(successful) / len(direction_cases),
                    "chrf": (
                        chrf.corpus_score(hypotheses, [references]).score
                        if successful
                        else None
                    ),
                    "bleu": (
                        bleu.corpus_score(hypotheses, [references]).score
                        if successful
                        else None
                    ),
                    "ter": (
                        ter.corpus_score(hypotheses, [references]).score
                        if successful
                        else None
                    ),
                    "latency_p50_ms": percentile(
                        (float(row["latency_ms"]) for row in successful), 50
                    ),
                    "latency_p95_ms": percentile(
                        (float(row["latency_ms"]) for row in successful), 95
                    ),
                    "numeric_recall": mean(numeric) if numeric else None,
                    "source_characters": sum(
                        int(row["source_characters"]) for row in direction_cases
                    ),
                    "severe_candidate_rate": (
                        mean(
                            1.0 if float(row.get("sentence_chrf", 0)) < 20 else 0.0
                            for row in successful
                        )
                        if successful
                        else None
                    ),
                }
                all_summaries.append(summary)
                print(
                    f"  {source}->{target}: {len(successful)}/{len(direction_cases)} "
                    f"chrF={summary['chrf']}",
                    flush=True,
                )
        resources = monitor.result()
        profile_resources.append(
            {
                "profile_key": profile_key,
                "profile_name": profile.name,
                "provider": profile.provider,
                "requests": len(profile_cases),
                "source_characters": sum(
                    int(row["source_characters"]) for row in profile_cases
                ),
                "resources": dataclass_dict(resources),
            }
        )
    write_csv(RESULTS_ROOT / "translation_cases.csv", all_cases)
    write_json(RESULTS_ROOT / "translation_summary.json", all_summaries)
    write_json(RESULTS_ROOT / "translation_resources.json", profile_resources)
    write_json(RESULTS_ROOT / "translation_availability.json", availability)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
