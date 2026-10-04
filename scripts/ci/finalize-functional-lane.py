"""Preserve BLOCKED fallback metadata and summarize bounded exact-run lane results."""
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

from functional_evidence import FIELDS, JOURNEY_FIELDS, STATES, expected_owners

MAX_LANE_BYTES = 65536
STATE_ORDER = ("PASS", "FAIL", "FLAKY", "SKIPPED", "QUARANTINED", "BLOCKED")
REFUSAL = "### Functional lane metadata: REFUSED\n\nFirst-attempt qualification: NO-GO (invalid, unbounded, or unexpected schema/run/owner data); no metadata was rendered.\n"


def unique_fields(pairs):
    data = {}
    for name, value in pairs:
        if name in data:
            raise ValueError("Duplicate metadata fields.")
        data[name] = value
    return data


def bounded_number(value, limit):
    return type(value) in (int, float) and 0 <= value < limit


def validate_summary_lane(data, sha, gate, domain, run_id, attempt):
    """Accept classified failure metadata for reporting, never for gate acceptance."""
    expected = expected_owners(domain, gate)
    if not isinstance(data, dict) or set(data) != FIELDS:
        raise ValueError("Unexpected metadata fields.")
    if type(data["schemaVersion"]) is not int or data["schemaVersion"] != 1:
        raise ValueError("Unsupported metadata schema.")
    if [data[field] for field in ("commitSha", "gate", "suite", "runId", "runAttempt")] != [sha, gate, domain, run_id, attempt]:
        raise ValueError("Metadata belongs to another execution.")
    for field in ("startedAt", "completedAt"):
        if not isinstance(data[field], str) or not re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{3})?Z", data[field]):
            raise ValueError("Invalid metadata timestamp.")
    if any(not bounded_number(data[field], 86400) for field in ("setupSeconds", "testSeconds")):
        raise ValueError("Invalid metadata duration.")
    journeys = data["journeys"]
    if not isinstance(journeys, list) or len(journeys) != len(expected):
        raise ValueError("Incomplete owner metadata.")
    seen = []
    for journey in journeys:
        if not isinstance(journey, dict) or set(journey) != JOURNEY_FIELDS:
            raise ValueError("Unexpected owner metadata fields.")
        if journey["journeyId"] not in expected or journey["status"] not in STATES:
            raise ValueError("Unknown owner or outcome.")
        # This rendering bound does not authorize retries; the gate validator is unchanged.
        if type(journey["attempts"]) is not int or not 0 <= journey["attempts"] <= 1000:
            raise ValueError("Invalid owner attempt count.")
        if not bounded_number(journey["durationMs"], 86400000):
            raise ValueError("Invalid owner duration.")
        seen.append(journey["journeyId"])
    if sorted(seen) != sorted(expected):
        raise ValueError("Missing or duplicate owners.")
    return data


def summarize_lane(data, diagnostics_present=False):
    domain, gate, attempt = data["suite"], data["gate"], data["runAttempt"]
    journeys = data["journeys"]
    counts = {state: sum(journey["status"] == state for journey in journeys) for state in STATE_ORDER}
    retries = sum(max(journey["attempts"] - 1, 0) for journey in journeys)
    qualification = ("All required owners recorded first-attempt PASS; aggregate acceptance remains separate."
                     if all(journey["status"] == "PASS" and journey["attempts"] == 1 for journey in journeys)
                     else "NO-GO (nonpassing, incomplete, or retried owner metadata).")
    lines = [f"### Functional {domain}", "", f"Candidate: `{data['commitSha']}`",
             f"Gate: `{gate}`; run: `{data['runId']}`; attempt: `{attempt}`", "",
             f"First-attempt qualification: {qualification}", "",
             "Gate decisions remain with the workflow aggregate.", "",
             "| Status | Count |", "| --- | ---: |"]
    lines.extend(f"| {state} | {counts[state]} |" for state in STATE_ORDER)
    lines.extend(["", f"Setup seconds: {data['setupSeconds']:.1f}", f"Test seconds: {data['testSeconds']:.1f}",
                  f"Retry count: {retries}", ""])
    for journey in journeys:
        lines.append(f"- {journey['journeyId']}: {journey['status']}; attempts={journey['attempts']}; duration={journey['durationMs']} ms")
    slowest = sorted(journeys, key=lambda journey: (-journey["durationMs"], journey["journeyId"]))[:3]
    lines.extend(["", "Slowest owners: " + "; ".join(f"{journey['journeyId']} {journey['durationMs'] / 1000:.1f}s" for journey in slowest), "",
                  f"Lane metadata artifact: `functional-lane-{gate}-{domain}-{attempt}` (planned subsequent upload)."])
    if diagnostics_present:
        lines.append(f"Container diagnostics artifact: `functional-diagnostics-{gate}-{domain}-{attempt}` (local snapshot present; upload outcome is recorded in the diagnostic upload step).")
    else:
        lines.append("Container diagnostics: no local snapshot present.")
    return "\n".join(lines) + "\n"


def main():
    exit_code = 0
    try:
        domain = os.environ["COGLATAS_FUNCTIONAL_DOMAIN"]
        gate = os.environ["COGLATAS_FUNCTIONAL_SELECTED_GATES"]
        sha = os.environ["TARGET_SHA"]
        run_id = os.environ["GITHUB_RUN_ID"]
        attempt = os.environ["GITHUB_RUN_ATTEMPT"]
        owners = expected_owners(domain, gate)
        if not re.fullmatch(r"[0-9a-f]{40}", sha) or not re.fullmatch(r"[1-9]\d{0,19}", run_id) or not re.fullmatch(r"[1-9]\d{0,8}", attempt):
            raise ValueError("Invalid execution identity.")
        path = Path(f"artifacts/functional/lane-{domain}.json")
        if not path.exists():
            now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            data = {"schemaVersion": 1, "commitSha": sha, "gate": gate, "runId": run_id, "runAttempt": attempt, "suite": domain,
                    "startedAt": now, "completedAt": now, "setupSeconds": 0, "testSeconds": 0,
                    "journeys": [{"journeyId": owner, "status": "BLOCKED", "attempts": 0, "durationMs": 0} for owner in owners]}
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        with path.open("rb") as source:
            raw = source.read(MAX_LANE_BYTES + 1)
        if len(raw) > MAX_LANE_BYTES:
            raise ValueError("Metadata exceeds the rendering budget.")
        data = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_fields)
        validate_summary_lane(data, sha, gate, domain, run_id, attempt)
        temporary = os.environ.get("RUNNER_TEMP")
        diagnostics_present = bool(temporary) and (Path(temporary) / f"functional-diagnostics-{domain}" / "functional-container-states.ndjson").is_file()
        summary = summarize_lane(data, diagnostics_present)
    except (KeyError, OSError, UnicodeError, ValueError, TypeError, RecursionError):
        summary = REFUSAL
        exit_code = 1
    print(summary, end="")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        try:
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as destination:
                destination.write(summary)
        except OSError:
            print("Functional lane summary could not be written.", file=sys.stderr)
            exit_code = 1
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
