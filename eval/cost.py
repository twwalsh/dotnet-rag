#!/usr/bin/env python3
"""Price the eval runs: one row per generator, the measured token counts times the list prices in eval/prices.json.

    python3 eval/cost.py eval/generators/*.json          # a markdown table, sorted by strict score then cost

The scores and tokens are what the runner measured; the prices are copied from the providers' pages (see the file
for the date); the dollars are the product, so they are computed, not billed. Cache reads and writes are priced
only for a provider that reports them (Claude); a run without the field is priced at the base input rate.
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PRICES = json.loads((HERE / "prices.json").read_text())["models"]
# The cases that call for no form, so a run from before the runner counted "forms offered unasked" can be counted here.
NO_FORM = {json.loads(l)["q"] for l in (HERE.parent / "data" / "eval.jsonl").read_text().splitlines() if l.strip() and json.loads(l).get("form") is None}


def price(run: dict) -> tuple[float, dict]:
    model = run.get("model") or "?"
    p = PRICES.get(model)
    totals = {k: sum(r["usage"].get(k, 0) for r in run["results"]) for k in ("inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens")}
    if p is None:
        return float("nan"), totals
    dollars = (
        totals["inputTokens"] * p["input"]
        + totals["outputTokens"] * p["output"]
        + totals["cacheReadTokens"] * p.get("cacheRead", p["input"])
        + totals["cacheWriteTokens"] * p.get("cacheWrite", p["input"])
    ) / 1_000_000
    return dollars, totals


def main(paths: list[str]) -> None:
    rows = []
    for path in paths:
        run = json.loads(Path(path).read_text())
        dollars, t = price(run)
        n = run["n"]
        seconds = sum(r["usage"]["seconds"] for r in run["results"]) / n
        unasked = sum(1 for r in run["results"] if r.get("form") and r["question"] in NO_FORM)
        rows.append((run.get("model") or "?", run["strict"], run["must"], run["mustNot"], run["forms"], unasked, n,
                     t["inputTokens"], t["cacheReadTokens"], t["outputTokens"], seconds, dollars, Path(path).name))
    rows.sort(key=lambda r: (-r[1], r[5], r[11]))
    print(f"| model | strict | must | must-not | form | forms unasked (of {len(NO_FORM)}) | input tok | cache reads | output tok | s/question | $ per run | $ per 1,000 questions | file |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for m, strict, must, must_not, forms, unasked, n, tin, tcache, tout, secs, dollars, name in rows:
        per_k = dollars / n * 1000
        print(f"| {m} | **{strict}/{n}** | {must}/{n} | {must_not}/{n} | {forms}/{n} | **{unasked}** | {tin:,} | {tcache:,} | {tout:,} | {secs:.2f} | ${dollars:.4f} | ${per_k:.2f} | {name} |")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])
