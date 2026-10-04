"""Validate bounded, secret-free Functional owner evidence before aggregating it."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

OWNERS = {
    "core": ["FUNC-TASK-001"],
    "files": ["FUNC-FILE-002"],
    "collaboration": ["FUNC-MSG-001", "FUNC-NOTIF-001"],
    "authz-negative": ["FUNC-AUTHZ-001", "FUNC-AUTHZ-002"],
}
GATES = {"functional-fast", "functional-full", "functional-extended"}
STATES = {"PASS", "FAIL", "FLAKY", "SKIPPED", "QUARANTINED", "BLOCKED"}
FIELDS = {
    "schemaVersion", "commitSha", "gate", "runId", "runAttempt", "suite",
    "startedAt", "completedAt", "setupSeconds", "testSeconds", "journeys",
}
JOURNEY_FIELDS = {"journeyId", "status", "attempts", "durationMs"}


def expected_owners(suite: str, gate: str) -> list[str]:
    if suite not in OWNERS or gate not in GATES:
        raise ValueError("Unknown Functional gate or domain.")
    return OWNERS[suite] + (["FUNC-ANN-001"] if suite == "collaboration" and gate != "functional-fast" else [])


def validate_lane(data: object, sha: str, gate: str, run_id: str, attempt: str) -> dict:
    if not isinstance(data, dict) or set(data) != FIELDS:
        raise ValueError("Functional lane fields do not match schema version 1.")
    if type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1:
        raise ValueError("Unsupported Functional evidence schema.")
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or data["commitSha"] != sha:
        raise ValueError("Functional evidence belongs to another commit.")
    if data["gate"] != gate or data["runId"] != str(run_id) or data["runAttempt"] != str(attempt):
        raise ValueError("Functional evidence belongs to another gate/run/attempt.")
    expected = expected_owners(data["suite"], gate)
    for field in ("startedAt", "completedAt"):
        if not isinstance(data[field], str) or not re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{3})?Z", data[field]):
            raise ValueError("Functional evidence has an invalid timestamp.")
    for field in ("setupSeconds", "testSeconds"):
        if type(data[field]) not in (float, int) or not 0 <= data[field] < 86400:
            raise ValueError("Functional evidence has an invalid duration.")
    journeys = data["journeys"]
    if not isinstance(journeys, list) or len(journeys) != len(expected):
        raise ValueError("Functional evidence is missing required owner results.")
    seen = []
    for journey in journeys:
        if not isinstance(journey, dict) or set(journey) != JOURNEY_FIELDS:
            raise ValueError("Functional journey fields do not match schema.")
        seen.append(journey["journeyId"])
        if journey["status"] not in STATES:
            raise ValueError("Functional journey result is unclassified.")
        if type(journey["attempts"]) is not int or journey["attempts"] < 0:
            raise ValueError("Functional journey attempts are invalid.")
        if type(journey["durationMs"]) not in (int, float) or not 0 <= journey["durationMs"] < 86400000:
            raise ValueError("Functional journey duration is invalid.")
        if journey["status"] != "PASS" or journey["attempts"] != 1:
            raise ValueError("Required Functional owner did not pass its first attempt.")
    if sorted(seen) != sorted(expected):
        raise ValueError("Functional owner coverage is missing, duplicated, or unexpected.")
    return data


def validate_manifest(data: object, sha: str, gate: str, run_id: str, attempt: str, suites: list[str] | None = None) -> dict:
    expected = list(OWNERS) if suites is None else suites
    if not expected or len(set(expected)) != len(expected) or any(suite not in OWNERS for suite in expected):
        raise ValueError("Functional domain selection is empty or invalid.")
    if not isinstance(data, dict) or set(data) != {"schemaVersion", "commitSha", "gate", "runId", "runAttempt", "lanes"}:
        raise ValueError("Functional aggregate fields do not match schema.")
    if type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1:
        raise ValueError("Unsupported Functional aggregate schema.")
    if [data[field] for field in ("commitSha", "gate", "runId", "runAttempt")] != [sha, gate, str(run_id), str(attempt)]:
        raise ValueError("Functional aggregate is stale or belongs to another run.")
    lanes = data["lanes"]
    if not isinstance(lanes, list) or len(lanes) != len(expected):
        raise ValueError("Functional aggregate has missing domain lanes.")
    validated = [validate_lane(lane, sha, gate, run_id, attempt) for lane in lanes]
    if sorted(lane["suite"] for lane in validated) != sorted(expected):
        raise ValueError("Functional aggregate has duplicate or unexpected lanes.")
    return data


def summarize(data: dict) -> str:
    count = sum(len(lane["journeys"]) for lane in data["lanes"])
    p0_count = sum(len(OWNERS[lane["suite"]]) for lane in data["lanes"])
    lines = [f"### {data['gate']}: GO", "", f"Candidate: `{data['commitSha']}`", "",
             f"{count} required owners ({p0_count} canonical P0 owners) passed their first attempt; 0 skipped, quarantined, blocked, or flaky.", "",
             "| Domain | Owners | Setup seconds | Test seconds |", "| --- | --- | ---: | ---: |"]
    for lane in data["lanes"]:
        lines.append(f"| {lane['suite']} | {', '.join(j['journeyId'] for j in lane['journeys'])} | {lane['setupSeconds']:.1f} | {lane['testSeconds']:.1f} |")
    slowest = sorted((journey for lane in data["lanes"] for journey in lane["journeys"]), key=lambda journey: journey["durationMs"], reverse=True)[:3]
    lines.extend(["", "Slowest owners: " + "; ".join(f"{journey['journeyId']} {journey['durationMs'] / 1000:.1f}s" for journey in slowest)])
    return "\n".join(lines) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--gate", required=True, choices=sorted(GATES))
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--attempt", required=True)
    parser.add_argument("--domains", default=json.dumps(list(OWNERS)))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()
    lanes = [json.loads(path.read_text(encoding="utf-8")) for path in args.directory.rglob("lane-*.json")]
    data = {"schemaVersion": 1, "commitSha": args.sha, "gate": args.gate, "runId": args.run_id,
            "runAttempt": args.attempt, "lanes": lanes}
    validate_manifest(data, args.sha, args.gate, args.run_id, args.attempt, json.loads(args.domains))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as destination:
            destination.write(summarize(data))
    print(summarize(data))


if __name__ == "__main__":
    main()
