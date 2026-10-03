"""Leave explicit BLOCKED metadata when setup or cancellation prevented owner execution."""
import json
import os
from datetime import datetime, timezone
from pathlib import Path

from functional_evidence import expected_owners

domain = os.environ["COGLATAS_FUNCTIONAL_DOMAIN"]
gate = os.environ["COGLATAS_FUNCTIONAL_SELECTED_GATES"]
path = Path(f"artifacts/functional/lane-{domain}.json")
if not path.exists():
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    data = {"schemaVersion": 1, "commitSha": os.environ["TARGET_SHA"], "gate": gate,
            "runId": os.environ["GITHUB_RUN_ID"], "runAttempt": os.environ["GITHUB_RUN_ATTEMPT"], "suite": domain,
            "startedAt": now, "completedAt": now, "setupSeconds": 0, "testSeconds": 0,
            "journeys": [{"journeyId": owner, "status": "BLOCKED", "attempts": 0, "durationMs": 0}
                         for owner in expected_owners(domain, gate)]}
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
if os.environ.get("GITHUB_STEP_SUMMARY"):
    data = json.loads(path.read_text(encoding="utf-8"))
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
        summary.write(f"### Functional {domain}\n\nCandidate: `{data['commitSha']}`\n\n")
        for journey in data["journeys"]:
            summary.write(f"- {journey['journeyId']}: {journey['status']}; attempts={journey['attempts']}; duration={journey['durationMs']} ms\n")
