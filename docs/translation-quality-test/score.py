#!/usr/bin/env python3
"""Score the translation-quality harness output.

The metric is a local character-n-gram chrF2 (beta=2, n=1..6, whitespace
removed). It is deliberately self-contained so the test package does not depend
on sacrebleu; use it for relative comparison on this dataset, not as an
absolute benchmark.
"""
from __future__ import annotations

import argparse
import csv
import json
import math
from collections import Counter
from pathlib import Path


def chrf2(hypothesis: str, reference: str, max_n: int = 6, beta: float = 2.0) -> float:
    hyp = "".join(hypothesis.split())
    ref = "".join(reference.split())
    if not hyp or not ref:
        return 0.0
    scores: list[float] = []
    for n in range(1, max_n + 1):
        h = Counter(hyp[i:i + n] for i in range(max(0, len(hyp) - n + 1)))
        r = Counter(ref[i:i + n] for i in range(max(0, len(ref) - n + 1)))
        if not h or not r:
            continue
        overlap = sum(min(h[key], r.get(key, 0)) for key in h)
        precision = overlap / sum(h.values())
        recall = overlap / sum(r.values())
        if precision <= 0 or recall <= 0:
            scores.append(0.0)
        else:
            beta2 = beta * beta
            scores.append((1 + beta2) * precision * recall / (beta2 * precision + recall))
    return sum(scores) / len(scores) if scores else 0.0


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, math.ceil(fraction * len(ordered)) - 1))
    return ordered[index]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raw", required=True)
    parser.add_argument("--out-dir", required=True)
    args = parser.parse_args()

    raw_path = Path(args.raw)
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    raw = json.loads(raw_path.read_text(encoding="utf-8"))
    items = {item["id"]: item for item in raw["items"]}
    variants = list(raw["variants"])
    results = raw["results"]

    rows_by_variant: dict[str, list[dict]] = {variant: [] for variant in variants}
    for row in results:
        item = items.get(row["itemId"])
        if item is None:
            continue
        row = dict(row)
        row["_item"] = item
        rows_by_variant.setdefault(row["variant"], []).append(row)

    sets = ["baseline", "conversation", "all"]
    summary: list[dict] = []
    for variant, rows in rows_by_variant.items():
        for subset in sets:
            selected = rows if subset == "all" else [row for row in rows if row["_item"]["set"] == subset]
            if not selected:
                continue
            scores = [chrf2(row["translation"], row["_item"]["gold"]) for row in selected]
            latencies = [float(row["latencyMs"]) for row in selected]
            failures = sum(1 for row in selected if row.get("error"))
            summary.append({
                "variant": variant,
                "set": subset,
                "n": len(selected),
                "chrf2": round(sum(scores) / len(scores), 2),
                "p50_ms": round(percentile(latencies, 0.50), 0),
                "p95_ms": round(percentile(latencies, 0.95), 0),
                "failures": failures,
            })

    order = {variant: index for index, variant in enumerate(variants)}
    summary.sort(key=lambda row: (sets.index(row["set"]), -row["chrf2"], order.get(row["variant"], 999)))

    with (out_dir / "summary.csv").open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=["variant", "set", "n", "chrf2", "p50_ms", "p95_ms", "failures"])
        writer.writeheader()
        writer.writerows(summary)

    lines = ["# Translation quality summary", "", f"Run: {raw.get('runAt', '')}", ""]
    for subset in ["baseline", "conversation", "all"]:
        subset_rows = [row for row in summary if row["set"] == subset]
        if not subset_rows:
            continue
        lines += [f"## {subset}", "", "| Variant | n | chrF2 | p50 ms | p95 ms | failures |", "| --- | ---: | ---: | ---: | ---: | ---: |"]
        for row in subset_rows:
            lines.append(f"| {row['variant']} | {row['n']} | {row['chrf2']:.2f} | {row['p50_ms']:.0f} | {row['p95_ms']:.0f} | {row['failures']} |")
        lines.append("")
    (out_dir / "summary.md").write_text("\n".join(lines), encoding="utf-8")

    example_lines = ["# Conversation examples", ""]
    for item in raw["items"]:
        if item["set"] != "conversation":
            continue
        example_lines += [f"## {item['id']} · {item['category']}", "", f"**原文**：{item['source']}", "", f"**参考**：{item['gold']}", ""]
        if item.get("context"):
            example_lines.append("**上文**：")
            for line in item["context"]:
                example_lines.append(f"- [{line['speaker']}] {line['text']}")
            example_lines.append("")
        for variant in variants:
            row = next((candidate for candidate in rows_by_variant.get(variant, []) if candidate["itemId"] == item["id"]), None)
            if row is None:
                continue
            error = f"（失败：{row['error']}）" if row.get("error") else ""
            example_lines.append(f"- **{variant}**：{row['translation']}{error}")
        example_lines.append("")
    (out_dir / "examples.md").write_text("\n".join(example_lines), encoding="utf-8")

    print(f"wrote {out_dir / 'summary.md'}")
    print(f"wrote {out_dir / 'examples.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
