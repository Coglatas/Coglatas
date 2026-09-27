#!/usr/bin/env python3
"""Prepare Qodana SARIF for stable GitHub Code Scanning ingestion."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any


def canonical_result(result: Any) -> str:
    return json.dumps(
        result,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    )


def main() -> int:
    if len(sys.argv) not in (3, 4):
        print(
            "Usage: prepare-qodana-sarif-for-github.py "
            "<input.sarif.json> <output.sarif.json> [category]",
            file=sys.stderr,
        )
        return 2

    input_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    requested_category = (
        sys.argv[3] if len(sys.argv) == 4 else "qodana-dotnet-community"
    )
    category = requested_category.rstrip("/")
    if not category:
        raise ValueError("GitHub Code Scanning category must not be empty.")

    with input_path.open("r", encoding="utf-8") as source:
        sarif = json.load(source)

    runs = sarif.get("runs")
    if sarif.get("version") != "2.1.0" or not isinstance(runs, list):
        raise ValueError("Expected a SARIF 2.1.0 document with a runs array.")

    removed_exact_duplicates = 0

    for run in runs:
        if not isinstance(run, dict):
            continue

        automation = dict(run.get("automationDetails") or {})
        # Use one stable analysis identity across full scans. Qodana can emit
        # report-specific automation ids/guids, which would fragment alerts.
        automation["id"] = f"{category}/"
        automation.pop("guid", None)
        run["automationDetails"] = automation

        results = run.get("results")
        if not isinstance(results, list):
            continue

        seen: set[str] = set()
        deduplicated: list[Any] = []
        for result in results:
            key = canonical_result(result)
            if key in seen:
                removed_exact_duplicates += 1
                continue
            seen.add(key)
            deduplicated.append(result)
        run["results"] = deduplicated

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as destination:
        json.dump(sarif, destination, ensure_ascii=False, indent=2)
        destination.write("\n")

    print(
        "Prepared Qodana SARIF for GitHub Code Scanning: "
        f"category={category}, "
        f"exactDuplicatesRemoved={removed_exact_duplicates}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
