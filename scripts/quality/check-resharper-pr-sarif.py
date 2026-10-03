#!/usr/bin/env python3
"""Fail a PR when ReSharper InspectCode reports issues in changed files."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from urllib.parse import unquote, urlparse

MAX_ANNOTATIONS = 50


def usage() -> None:
    print(
        "Usage: check-resharper-pr-sarif.py <inspectcode.sarif.json> <changed-files.nul>",
        file=sys.stderr,
    )


def read_changed_files(path: Path) -> set[str]:
    if not path.is_file():
        raise FileNotFoundError(f"changed-file inventory not found: {path}")

    changed: set[str] = set()
    for item in path.read_bytes().split(b"\0"):
        if not item:
            continue
        value = item.decode("utf-8", errors="surrogateescape").replace("\\", "/")
        while value.startswith("./"):
            value = value[2:]
        changed.add(value)
    return changed


def normalize_uri(uri: str, workspace: Path) -> str | None:
    decoded = unquote(uri)
    parsed = urlparse(decoded)

    if parsed.scheme and parsed.scheme != "file":
        return None

    raw = parsed.path if parsed.scheme == "file" else decoded
    if parsed.scheme == "file" and parsed.netloc:
        raw = f"//{parsed.netloc}{raw}"

    raw = raw.replace("\\", "/")
    candidate = Path(raw)

    if candidate.is_absolute():
        try:
            candidate = candidate.resolve().relative_to(workspace.resolve())
        except ValueError:
            return None

    normalized = candidate.as_posix()
    while normalized.startswith("./"):
        normalized = normalized[2:]
    return normalized


def escape_message(value: str) -> str:
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(value: str) -> str:
    return escape_message(value).replace(":", "%3A").replace(",", "%2C")


def first_location(result: dict, workspace: Path) -> tuple[str, int, int] | None:
    for location in result.get("locations") or []:
        physical = location.get("physicalLocation") or {}
        artifact = physical.get("artifactLocation") or {}
        uri = artifact.get("uri")
        if not isinstance(uri, str) or not uri:
            continue

        path = normalize_uri(uri, workspace)
        if not path:
            continue

        region = physical.get("region") or {}
        line = int(region.get("startLine") or 1)
        column = int(region.get("startColumn") or 1)
        return path, line, column

    return None


def main() -> int:
    if len(sys.argv) != 3:
        usage()
        return 2

    sarif_path = Path(sys.argv[1])
    changed_path = Path(sys.argv[2])
    workspace = Path(os.environ.get("GITHUB_WORKSPACE", os.getcwd()))

    if not sarif_path.is_file():
        print(f"::error::ReSharper SARIF report not found: {escape_message(str(sarif_path))}")
        return 2

    changed = read_changed_files(changed_path)

    with sarif_path.open("r", encoding="utf-8") as handle:
        sarif = json.load(handle)

    runs = sarif.get("runs")
    if not isinstance(runs, list) or not runs:
        print("::error::ReSharper SARIF contains no runs.")
        return 2

    findings: list[tuple[str, int, int, str, str]] = []
    total_results = 0

    for run in runs:
        invocation_list = run.get("invocations") or []
        for invocation in invocation_list:
            if invocation.get("executionSuccessful") is False:
                print("::error::ReSharper SARIF reports an unsuccessful analysis invocation.")
                return 2

        for result in run.get("results") or []:
            total_results += 1
            location = first_location(result, workspace)
            if location is None:
                continue

            path, line, column = location
            if path not in changed:
                continue

            rule_id = str(result.get("ruleId") or "inspection")
            message = result.get("message") or {}
            text = str(message.get("text") or message.get("markdown") or "ReSharper inspection finding")
            findings.append((path, line, column, rule_id, text))

    for path, line, column, rule_id, message in findings[:MAX_ANNOTATIONS]:
        print(
            "::error "
            f"file={escape_property(path)},line={line},col={column},"
            f"title={escape_property('ReSharper ' + rule_id)}::"
            f"{escape_message(message)}"
        )

    print(
        f"ReSharper InspectCode reported {total_results} WARNING-or-higher result(s); "
        f"{len(findings)} are in files changed by this pull request."
    )

    if len(findings) > MAX_ANNOTATIONS:
        print(
            f"Only the first {MAX_ANNOTATIONS} findings were emitted as GitHub annotations; "
            "the uploaded SARIF artifact contains the complete report."
        )

    return 1 if findings else 0


if __name__ == "__main__":
    raise SystemExit(main())
