#!/usr/bin/env python3
"""Route PERF-05 conservatively and resolve exact-main build artifacts."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from common import PerformanceContractError, write_json_atomic


def sha(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value):
        raise PerformanceContractError("invalid target SHA")
    return value


def relevant(path):
    # Unknown files run the gate. Only established unrelated surfaces can skip.
    if path.startswith(("src/", "tests/Coglatas.Tests/", "scripts/performance/", "performance/",
                        "infra/compose/performance/", ".github/", "scripts/ci/")):
        return True
    if path.startswith(("frontend/", "tests/ui/", "docs/")) or ("/" not in path and path.endswith(".md")):
        return False
    return True


def route(head, base, event, git=subprocess.run):
    head = sha(head)
    actual = git(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    if actual != head:
        raise PerformanceContractError("checkout does not match target SHA")
    result = {"schemaVersion": 1, "headSha": head, "baseSha": None, "event": event,
              "required": True, "reasonCode": "conservative-no-diff", "changedFileCount": None,
              "changedPathsHash": None}
    if event not in ("pull_request", "push") or not base or base == "0" * 40:
        return result
    try:
        sha(base)
        diff = git(["git", "diff", "--name-only", "-z", base + "..." + head], capture_output=True, check=True)
        paths = [p.decode("utf-8", errors="strict") for p in diff.stdout.split(b"\0") if p]
    except (subprocess.CalledProcessError, UnicodeError, PerformanceContractError):
        result["reasonCode"] = "conservative-diff-failure"
        return result
    result.update(baseSha=base, changedFileCount=len(paths), changedPathsHash=hashlib.sha256(diff.stdout).hexdigest())
    result["required"] = not paths or any(relevant(p) for p in paths)
    result["reasonCode"] = "relevant-change" if result["required"] else "validated-not-applicable"
    return result


def choose_build(runs, head):
    sha(head)
    # A successfully assembled artifact remains useful if a later validation lane
    # fails. Its own producer job and source stamp must still match exactly.
    return [r for r in runs if r.get("head_sha") == head and r.get("head_branch") == "main"
            and r.get("event") == "push" and r.get("status") == "completed"
            and r.get("path") == ".github/workflows/main-build-artifacts.yml"
            and type(r.get("id")) is int]


def resolve_build(repository, head, api=None):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository or ""):
        raise PerformanceContractError("invalid repository identity")
    head = sha(head)
    if api is None:
        token = os.environ.get("GH_TOKEN")
        if not token:
            raise PerformanceContractError("build metadata token missing")
        def api(path):
            request = urllib.request.Request("https://api.github.com/repos/" + repository + path,
                                            headers={"Authorization": "Bearer " + token,
                                                     "Accept": "application/vnd.github+json",
                                                     "X-GitHub-Api-Version": "2022-11-28"})
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
    query = urllib.parse.urlencode({"branch": "main", "head_sha": head, "status": "completed", "per_page": 30})
    runs = api("/actions/workflows/main-build-artifacts.yml/runs?" + query).get("workflow_runs", [])
    for run in choose_build(runs, head):
        jobs = api(f"/actions/runs/{run['id']}/jobs?filter=latest&per_page=100").get("jobs", [])
        if not any(j.get("name") == "Main runtime artifact assembler" and j.get("conclusion") == "success" for j in jobs):
            continue
        artifacts = api(f"/actions/runs/{run['id']}/artifacts?name=main-build-artifacts&per_page=100").get("artifacts", [])
        matches = [a for a in artifacts if a.get("name") == "main-build-artifacts" and a.get("expired") is False
                   and a.get("workflow_run", {}).get("head_sha") == head]
        if len(matches) == 1:
            return str(run["id"])
    raise PerformanceContractError("exact-main build artifact unavailable")


def gate_decision(routing, collector_result, measurement, expected):
    expected = sha(expected)
    if routing.get("schemaVersion") != 1 or routing.get("headSha") != expected or type(routing.get("required")) is not bool:
        raise PerformanceContractError("invalid route evidence")
    if not routing["required"]:
        if routing.get("reasonCode") != "validated-not-applicable" or collector_result != "skipped":
            raise PerformanceContractError("invalid not-applicable lane")
        return "not-applicable"
    if collector_result != "success" or not isinstance(measurement, dict) or measurement.get("headSha") != expected:
        raise PerformanceContractError("missing or incompatible collector evidence")
    if measurement.get("schemaVersion") != 1 or measurement.get("decision") not in ("pass", "regression", "invalid", "unstable", "insufficient-data"):
        raise PerformanceContractError("required DB gate failed")
    return measurement["decision"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("route", "resolve-build", "gate"))
    parser.add_argument("--head", required=True)
    parser.add_argument("--base")
    parser.add_argument("--event", default="workflow_dispatch")
    parser.add_argument("--repository")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--route", type=Path)
    parser.add_argument("--measurement", type=Path)
    parser.add_argument("--collector-result")
    args = parser.parse_args()
    try:
        if args.operation == "resolve-build":
            print(resolve_build(args.repository, args.head))
        elif args.operation == "route":
            result = route(args.head, args.base, args.event)
            write_json_atomic(args.output, result)
            print(str(result["required"]).lower())
        else:
            routing = json.loads(args.route.read_text(encoding="utf-8"))
            measurement = None if not args.measurement or not args.measurement.exists() else json.loads(args.measurement.read_text(encoding="utf-8"))
            decision = gate_decision(routing, args.collector_result, measurement, args.head)
            output = {"schemaVersion": 1, "headSha": sha(args.head), "decision": decision,
                      "route": routing, "workflowRunId": os.environ.get("GITHUB_RUN_ID"),
                      "workflowRunAttempt": os.environ.get("GITHUB_RUN_ATTEMPT")}
            if measurement:
                for key in ("contractHash", "environmentFingerprintHashes", "fixtureHashes", "results", "durationDecision"):
                    if key in measurement:
                        output[key] = measurement[key]
            write_json_atomic(args.output, output)
            print(decision)
            return 0 if decision in ("pass", "not-applicable") else 1
        return 0
    except (PerformanceContractError, OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError,
            urllib.error.URLError):
        if args.operation == "gate" and args.output:
            write_json_atomic(args.output, {"schemaVersion": 1, "headSha": args.head,
                                          "decision": "invalid", "reasonCode": "required-evidence-failed"})
        # Token, HTTP responses, command output and protected URLs never enter logs.
        print("PERF-05 CI failed: invalid routing, target, producer, or gate evidence", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
