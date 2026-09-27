#!/usr/bin/env python3
from __future__ import annotations

import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

HISTORICAL_PREFIXES = (
    "docs/evidence/",
    "docs/archive/",
    "docs/verification/",
    "docs/audit/",
)
HISTORICAL_SUFFIXES = (".log",)
SKIP_PATHS = {"scripts/ci/check-coglatas-rename.py"}

# These files intentionally preserve source-era repository names, browser-storage
# keys, and UI-baseline identifiers so the Avalonia cutover can identify and
# safely migrate or retire persisted Angular state. Keep this allowlist exact;
# active product/runtime assets remain covered by the rename guard.
LEGACY_MIGRATION_EVIDENCE_PATHS = {
    "docs/migration/avalonia/angular-frontend-inventory.json",
    "docs/migration/avalonia/angular-frontend-inventory.md",
    "docs/migration/avalonia/angular-persisted-state-inventory.json",
    "docs/migration/avalonia/angular-target-surface-map-v5.8.1.json",
}

PROTECTED_LITERALS = (
    "NYGsatoshi/AIPsiteNYGspec",
    "AIPsiteNYGspec",
    "nygsatoshi/aipsitenygspec",
    "aipsitenygspec",
)
PROTECTED_SPEC_PATH = re.compile(r"docs/specs/aip-core-v4/[A-Za-z0-9_./+@-]+")

PATH_PATTERN = re.compile(
    r"AipPortal|AIPsiteNYG|AIPsite|AipSite|aipsite|aipportal|"
    r"(?<![A-Za-z0-9])aip[-_.]|(?<![A-Za-z0-9])AIP_"
)
CONTENT_PATTERNS = (
    re.compile(r"AipPortal|AIPPORTAL|AIP_PORTAL|aip_portal|aipportal"),
    re.compile(r"AIPsiteNYG|AIPsite|AipSite|AIPSITE|aipsite"),
    re.compile(r"\bAIP_"),
    re.compile(r"\bAIP-"),
    re.compile(r"\bAIP(?=[A-Z0-9])"),
    re.compile(r"\bAip(?=[A-Z0-9])"),
    re.compile(r"\baip-"),
    re.compile(r"\baip_"),
    re.compile(r"\baip\."),
)

def tracked_files() -> list[str]:
    raw = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT)
    return [
        item.decode("utf-8", errors="surrogateescape")
        for item in raw.split(b"\0")
        if item
    ]

def is_historical(path: str) -> bool:
    return path.startswith(HISTORICAL_PREFIXES) or path.endswith(HISTORICAL_SUFFIXES)

def protect_external_spec_refs(text: str) -> str:
    protected = text
    for index, literal in enumerate(PROTECTED_LITERALS):
        protected = protected.replace(literal, f"__COGLATAS_SPEC_REF_{index}__")
    protected = PROTECTED_SPEC_PATH.sub("__COGLATAS_SPEC_PATH__", protected)
    return protected

def main() -> int:
    findings: list[str] = []

    for path in tracked_files():
        if is_historical(path) or path in SKIP_PATHS or path in LEGACY_MIGRATION_EVIDENCE_PATHS:
            continue

        if PATH_PATTERN.search(path):
            findings.append(f"path: {path}")

        file_path = ROOT / path
        if not file_path.is_file():
            continue

        try:
            text = file_path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue

        candidate = protect_external_spec_refs(text)
        for line_number, line in enumerate(candidate.splitlines(), start=1):
            if any(pattern.search(line) for pattern in CONTENT_PATTERNS):
                findings.append(f"{path}:{line_number}: {line.strip()}")

    if findings:
        print("Legacy AIP/AIPsite identifiers remain in active repository assets:")
        for finding in findings[:500]:
            print(f"- {finding}")
        if len(findings) > 500:
            print(f"... and {len(findings) - 500} more finding(s)")
        return 1

    print(
        "Coglatas rename guard passed: no legacy AIP/AIPsite identifiers remain "
        "in active repository paths or UTF-8 text assets."
    )
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
