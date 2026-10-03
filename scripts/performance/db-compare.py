#!/usr/bin/env python3
"""Fail-closed aggregation of both PERF-05 datasets and PERF-03 duration adapters."""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

from common import PerformanceContractError, load_json, repository_root, write_json_atomic, fixture_hash
from db_gate import capture_failures, growth_failures, validate_contract
from compare import compare_documents, summarize, environment_compatibility_key


def evaluate(small, medium, contract):
    for profile, expected in ((small, "small"), (medium, "medium")):
        if profile.get("schemaVersion") != 1 or profile.get("fixtureVersion") != 1 or profile.get("fixtureHash") != fixture_hash(expected):
            raise PerformanceContractError("incompatible fixture/schema identity")
        if not re.fullmatch(r"[0-9a-f]{40}", profile.get("headSha", "")):
            raise PerformanceContractError("invalid head identity")
    if any(p.get("collectionComplete") is not True for p in (small, medium)):
        raise PerformanceContractError("incomplete DB collection")
    if small["profile"] != "small" or medium["profile"] != "medium" or small["headSha"] != medium["headSha"]:
        raise PerformanceContractError("incompatible dataset/head pair")
    if small["fixtureVersion"] != medium["fixtureVersion"] or not all(p.get("warmupSamplesExcluded") is True for p in (small, medium)):
        raise PerformanceContractError("incompatible fixture/measurement version")
    results = []
    for scenario in contract["scenarios"]:
        records = []
        for profile in (small, medium):
            matches = [s for s in profile["scenarios"] if s["id"] == scenario["id"]]
            if len(matches) != 1:
                raise PerformanceContractError("missing or duplicated scenario")
            record = matches[0]
            expected_keys = {(size, page, iteration) for size in (contract["policy"]["pageSizes"] if scenario["paged"] else [0]) for page in ((1, 2) if scenario["paged"] else (1,)) for iteration in range(1, contract["policy"]["samples"] + 1)}
            actual_keys = {(s["pageSize"], s["page"], s["iteration"]) for s in record["samples"]}
            if actual_keys != expected_keys or len(record["samples"]) != len(expected_keys):
                raise PerformanceContractError("missing/duplicate measured sample")
            failures = set(record["failures"])
            for sample in record["samples"]:
                failures.update(capture_failures(sample["capture"], scenario, sample["pageSize"], contract["policy"]))
            records.append(record)
            counts = [s["capture"]["commandCount"] for s in record["samples"]]
            results.append({"scenario": scenario["id"], "profile": profile["profile"], "metric": "db.query_count", "sampleCount": len(counts), "summary": summarize(counts), "failures": sorted(failures), "decision": "regression" if failures else "pass"})
        growth = growth_failures(*records, contract["policy"])
        results.append({"scenario": scenario["id"], "metric": "db.query_growth", "failures": growth, "decision": "regression" if growth else "pass"})
    for check in contract["planChecks"]:
        matches = [p for p in medium["plans"] if p["id"] == check["id"]]
        if len(matches) != 1 or matches[0]["tableRows"] < check["minimumTableRows"] or type(matches[0]["requiredIndexPresent"]) is not bool:
            raise PerformanceContractError("missing/invalid selected plan invariant")
        satisfied = matches[0]["requiredIndexPresent"]
        results.append({"scenario": check["id"], "metric": "db.plan_invariant", "decision": "pass" if satisfied else "regression", "failures": [] if satisfied else ["required-index-missing"]})
    return {"schemaVersion": 1, "headSha": small["headSha"], "decision": "regression" if any(r["decision"] != "pass" for r in results) else "pass", "results": results}


def duration_results(profile, fingerprint, root, baselines=None):
    if fingerprint.get("commitSha") != profile["headSha"] or fingerprint.get("fixture", {}).get("hash") != profile["fixtureHash"]:
        raise PerformanceContractError("DB duration fingerprint mismatch")
    environment_compatibility_key(fingerprint)
    outputs = []
    for measurement in profile["measurements"]:
        # PERF-03's comparison is scenario-specific; keep the page-5 stream canonical.
        # Page-10 samples remain in raw evidence and are never mixed into that stream.
        if measurement["pageSize"] not in (0, 5):
            continue
        baseline = {} if baselines is None else load_json(baselines / profile["profile"] / (measurement["scenario"] + ".json"))
        result = compare_documents(measurement, baseline, fingerprint, load_json(root / "performance/scenarios.json"), load_json(root / "performance/budgets.json"), load_json(root / "performance/environment.json"), load_json(root / "performance/comparison-policy.json"))
        outputs.append(result)
    return outputs


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--small", type=Path, required=True)
    parser.add_argument("--medium", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--duration", action="store_true")
    parser.add_argument("--baselines", type=Path)
    args = parser.parse_args()
    try:
        root = repository_root()
        contract = load_json(root / "performance/db-scenarios.json")
        validate_contract(contract, load_json(root / "performance/scenarios.json"))
        small, medium = load_json(args.small), load_json(args.medium)
        result = evaluate(small, medium, contract)
        write_json_atomic(args.output, result)
        if args.duration:
            for path, profile in ((args.small, small), (args.medium, medium)):
                fingerprint = load_json(path.parent / "environment.json")
                values = duration_results(profile, fingerprint, root, args.baselines)
                write_json_atomic(args.output.parent / (profile["profile"] + "-duration-results.json"), {"results": values})
                # No approved baseline is present in main today. Preserve PERF-03's
                # invalid/missing-baseline decisions instead of approving PR samples.
                if args.baselines and any(v["decision"] != "pass" for v in values):
                    return 1
        print(json.dumps({"decision": result["decision"], "failingChecks": sum(r["decision"] != "pass" for r in result["results"])}, sort_keys=True))
        return 0 if result["decision"] == "pass" else 1
    except (PerformanceContractError, OSError, ValueError, KeyError, TypeError):
        print("PERF-05 comparison failed: incomplete, unsafe, or incompatible evidence", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
