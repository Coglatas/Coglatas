#!/usr/bin/env python3
"""Fail-closed aggregation for merge-required GitHub Actions jobs."""
from __future__ import annotations

import json
import os
import sys
from typing import Any


def main() -> int:
    raw = os.environ.get("REQUIRED_NEEDS_JSON", "")
    if not raw:
        print("::error::REQUIRED_NEEDS_JSON is missing.", file=sys.stderr)
        return 1

    try:
        needs: Any = json.loads(raw)
    except json.JSONDecodeError as exc:
        print(f"::error::REQUIRED_NEEDS_JSON is malformed JSON: {exc}", file=sys.stderr)
        return 1

    if not isinstance(needs, dict) or not needs:
        print("::error::Required aggregate job has no dependency results.", file=sys.stderr)
        return 1

    failures: list[str] = []
    for name in sorted(needs):
        value = needs[name]
        if not isinstance(value, dict):
            failures.append(f"{name}=malformed")
            continue
        result = value.get("result")
        if result != "success":
            failures.append(f"{name}={result!r}")

    if failures:
        print(
            "::error::Parallel required-check worker failure: " + ", ".join(failures),
            file=sys.stderr,
        )
        return 1

    print("All parallel required-check workers completed successfully.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
