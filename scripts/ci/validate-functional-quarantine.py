"""Require reviewable, expiring quarantine records without authorizing owner skips."""
import json
import re
from datetime import date
from pathlib import Path

FIELDS = {"issue", "reason", "owner", "domain", "quarantinedAt", "reviewBy", "journeyId", "p0ReadinessImpact"}


def validate(data, today=None):
    today = today or date.today()
    if not isinstance(data, dict) or set(data) != {"schemaVersion", "entries"} or type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1 or not isinstance(data["entries"], list):
        raise ValueError("Invalid Functional quarantine registry schema.")
    seen = set()
    for entry in data["entries"]:
        if not isinstance(entry, dict) or set(entry) != FIELDS or any(not isinstance(value, str) or not value.strip() for value in entry.values()):
            raise ValueError("Functional quarantine requires Issue, reason, owner, domain, dates, Journey ID, and readiness impact.")
        if not re.fullmatch(r"https://github\.com/NYGsatoshi/Coglatas/issues/[1-9]\d*", entry["issue"]):
            raise ValueError("Functional quarantine requires a repository tracking Issue.")
        if not re.fullmatch(r"FUNC-[A-Z]+-\d{3}", entry["journeyId"]) or entry["journeyId"] in seen:
            raise ValueError("Functional quarantine Journey ID is invalid or duplicated.")
        seen.add(entry["journeyId"])
        if entry["domain"] not in {"core", "files", "collaboration", "authz-negative"}:
            raise ValueError("Functional quarantine domain must have an explicit execution owner.")
        started = date.fromisoformat(entry["quarantinedAt"])
        review = date.fromisoformat(entry["reviewBy"])
        if started > today or review < today or review < started:
            raise ValueError("Functional quarantine is future-dated or expired; review is required.")
    return len(seen)


if __name__ == "__main__":
    count = validate(json.loads(Path("tests/functional/quarantine.json").read_text(encoding="utf-8")))
    print(f"Functional quarantine registry validated: {count} entries; required owner skips remain prohibited.")
