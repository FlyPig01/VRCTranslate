from __future__ import annotations

import time
from typing import Any
from uuid import uuid4

from benchmarks.benchmark_config import LANGUAGES, RESULTS_ROOT, TMP_ROOT, ensure_directories
from benchmarks.common import mean, write_csv, write_json
from benchmarks.run_translation_benchmark import _profiles, _translate_with_retry
from vrctranslate.application.use_cases.translate_text import TranslateText
from vrctranslate.domain.translation import TranslationRequest
from vrctranslate.infrastructure.glossary import JsonGlossaryRepository
from vrctranslate.infrastructure.text.wanakana_converter import WanaKanaRomajiConverter
from vrctranslate.infrastructure.translation.router import TranslationRouter


SENTENCES = {
    "zh-CN": "请在 VRChat 中用 OSC 联系 Player_01，房间编号是 42，地址是 https://example.com/world?id=42。",
    "zh-TW": "請在 VRChat 中用 OSC 聯絡 Player_01，房間編號是 42，網址是 https://example.com/world?id=42。",
    "en": "Contact Player_01 through OSC in VRChat. Room 42 is at https://example.com/world?id=42.",
    "ja": "VRChatでOSCを使ってPlayer_01に連絡してください。ルーム番号は42、URLはhttps://example.com/world?id=42です。",
    "ko": "VRChat에서 OSC로 Player_01에게 연락하세요. 방 번호는 42이고 주소는 https://example.com/world?id=42입니다.",
    "fr": "Contactez Player_01 via OSC dans VRChat. La salle 42 se trouve à https://example.com/world?id=42.",
    "de": "Kontaktiere Player_01 über OSC in VRChat. Raum 42 ist unter https://example.com/world?id=42 erreichbar.",
    "es": "Contacta con Player_01 mediante OSC en VRChat. La sala 42 está en https://example.com/world?id=42.",
    "ru": "Свяжитесь с Player_01 через OSC в VRChat. Комната 42 находится по адресу https://example.com/world?id=42.",
}
INVARIANTS = ("VRChat", "OSC", "Player_01", "42", "https://example.com/world?id=42")


def main() -> int:
    ensure_directories()
    profile_items, _availability = _profiles()
    profile_items = [
        item for item in profile_items if item[1].provider in {"tencent", "aliyun"}
    ]
    rows: list[dict[str, Any]] = []
    repository = JsonGlossaryRepository(TMP_ROOT / "benchmark-user-glossary.json")
    for profile_key, profile, adapter in profile_items:
        use_case = TranslateText(
            TranslationRouter([adapter]),
            WanaKanaRomajiConverter(),
            repository,
        )
        for spec in LANGUAGES:
            target = "en" if spec.code == "zh-CN" else "zh-CN"
            request = TranslationRequest(
                uuid4().hex,
                SENTENCES[spec.code],
                spec.code,
                target,
                "self",
            )
            started = time.perf_counter()
            try:
                result = use_case.execute(request, profile)
                translated = result.translated
                status = "ok"
                error = ""
            except Exception as exc:
                translated = ""
                status = "failed"
                error = str(getattr(exc, "user_message", type(exc).__name__))[:200]
            preserved = {token: token in translated for token in INVARIANTS}
            rows.append(
                {
                    "profile_key": profile_key,
                    "profile_name": profile.name,
                    "provider": profile.provider,
                    "source_language": spec.code,
                    "target_language": target,
                    "source": request.text,
                    "translated": translated,
                    "status": status,
                    "latency_ms": (time.perf_counter() - started) * 1000,
                    "glossary_status": use_case.glossary_status(profile.id),
                    "preserved_count": sum(preserved.values()),
                    "preservation_rate": mean(
                        1.0 if value else 0.0 for value in preserved.values()
                    ),
                    "missing": ", ".join(
                        token for token, value in preserved.items() if not value
                    ),
                    "error": error,
                }
            )
            time.sleep(0.23 if profile.provider == "tencent" else 0.05)
        print(f"[domain] {profile.name}: complete", flush=True)
    summaries: list[dict[str, Any]] = []
    for profile_key, profile, _adapter in profile_items:
        subset = [row for row in rows if row["profile_key"] == profile_key]
        summaries.append(
            {
                "profile_key": profile_key,
                "profile_name": profile.name,
                "provider": profile.provider,
                "cases": len(subset),
                "success_rate": mean(1.0 if row["status"] == "ok" else 0.0 for row in subset),
                "invariant_preservation_rate": mean(
                    float(row["preservation_rate"]) for row in subset
                ),
                "glossary_compatible_rate": mean(
                    1.0 if row["glossary_status"] == "compatible" else 0.0
                    for row in subset
                ),
            }
        )
    write_csv(RESULTS_ROOT / "domain_invariant_cases.csv", rows)
    write_json(RESULTS_ROOT / "domain_invariant_summary.json", summaries)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
