#!/usr/bin/env python3
"""Exercise real authenticated list APIs inside PERF-02; export only allowlisted DB evidence."""
from __future__ import annotations

import argparse
import http.cookiejar
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

from common import PerformanceContractError, load_json, validate_fixture_evidence, validate_target, write_json_atomic, repository_root
from db_gate import capture_failures, fingerprint_counts, index_kinds, plan_invariant, validate_capture, validate_contract


def request(opener, base, route, headers, data=None):
    req = urllib.request.Request(base + route, headers=headers, data=None if data is None else json.dumps(data).encode())
    try:
        with opener.open(req, timeout=120) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        # Never surface error bodies, headers, request URL, cookie, or credentials.
        return error.code, None


def login(opener, base, fixture):
    headers = {"X-Tenant-Slug": fixture["identities"]["tenantSlug"], "Content-Type": "application/json"}
    status, csrf = request(opener, base, "/api/security/csrf-token", headers)
    if status != 200 or not isinstance(csrf, dict) or not csrf.get("token"):
        raise PerformanceContractError("DB probe CSRF bootstrap failed")
    headers[csrf["headerName"]] = csrf["token"]
    password = os.environ.get("COGLATAS_PERFORMANCE_PASSWORD")
    if not password:
        raise PerformanceContractError("synthetic fixture password missing")
    status, _ = request(opener, base, "/api/auth/login", headers, {"email": fixture["identities"]["operatorEmail"], "password": password})
    if status != 200:
        raise PerformanceContractError("DB probe login failed")
    return {"X-Tenant-Slug": headers["X-Tenant-Slug"]}


def page_identity(payload, scenario):
    if not isinstance(payload, (dict, list)):
        raise PerformanceContractError("DB probe response is not a collection")
    items = payload if isinstance(payload, list) else payload.get("items")
    if not isinstance(items, list):
        raise PerformanceContractError("DB probe response has no items")
    identities = [item.get("id", item.get("taskId", item.get("workspaceId"))) for item in items]
    if any(not isinstance(value, str) for value in identities) or len(set(identities)) != len(identities):
        raise PerformanceContractError("invalid or duplicate response identity")
    # Identities and timestamp stay in memory for repeat/disjoint-page assertions.
    before = items[-1].get("createdAt") if items and scenario.get("cursor") else None
    return identities, before


def plan_check(check, fixture, command):
    task_id = str(uuid.UUID(fixture["identities"]["taskId"]))
    # Relation/query are fixed source-owned hot paths, not incoming SQL/identifiers.
    sql = f'''ANALYZE task_items;
SELECT count(*) FROM task_items;
EXPLAIN (FORMAT JSON) SELECT * FROM task_items WHERE "Id" = '{task_id}'::uuid;'''
    result = subprocess.run(command + ["-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1"], input=sql, text=True, capture_output=True, timeout=60)
    if result.returncode:
        raise PerformanceContractError("selected EXPLAIN command failed")
    count_text, plan_text = result.stdout.strip().split("\n", 1)
    count = int(count_text)
    if count < check["minimumTableRows"]:
        raise PerformanceContractError("selected plan fixture is too small")
    plan = json.loads(plan_text)
    satisfied = plan_invariant(plan, check["relation"], check["keyColumn"])
    # Do not save query text, conditions, planner literals, or the full plan.
    def node_types(value):
        if isinstance(value, list):
            return set().union(*(node_types(item) for item in value))
        if not isinstance(value, dict):
            return set()
        allowed = {"Index Scan", "Index Only Scan", "Bitmap Index Scan", "Bitmap Heap Scan", "Seq Scan", "Result", "Gather"}
        own = {value["Node Type"]} if value.get("Node Type") in allowed else set()
        return own | set().union(*(node_types(item) for key, item in value.items() if key in {"Plan", "Plans"}))
    return {"id": check["id"], "tableRows": count, "requiredKeyLookupPresent": satisfied, "observedNodeTypes": sorted(node_types(plan)), "observedIndexKinds": index_kinds(plan, check["relation"]), "decision": "pass" if satisfied else "regression"}


def collect(args, contract):
    base = validate_target(os.environ["COGLATAS_PERFORMANCE_BASE_URL"])
    fixture = validate_fixture_evidence(load_json(Path(os.environ["COGLATAS_PERFORMANCE_FIXTURE_EVIDENCE"])), args.profile)
    fingerprint = load_json(Path(os.environ["COGLATAS_PERFORMANCE_ENVIRONMENT_EVIDENCE"]))
    if fingerprint.get("fixture", {}).get("hash") != fixture["fixtureHash"]:
        raise PerformanceContractError("fixture/fingerprint mismatch")
    if not __import__("re").fullmatch(r"[0-9a-f]{40}", fingerprint.get("commitSha", "")):
        raise PerformanceContractError("missing head identity")
    capture_dir = Path(os.environ["COGLATAS_PERFORMANCE_DB_EVIDENCE_PATH"])
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    headers = login(opener, base, fixture)
    policy = contract["policy"]
    output = {"schemaVersion": 1, "headSha": fingerprint["commitSha"], "profile": args.profile, "fixtureHash": fixture["fixtureHash"], "fixtureVersion": fixture["fixtureVersion"], "warmupSamplesExcluded": True, "collectionComplete": False, "scenarios": [], "plans": [], "measurements": []}
    write_json_atomic(args.output, output)
    for scenario in contract["scenarios"]:
        cardinality = fixture["cardinalities"]["workspaces"] if scenario["focus"] == "workspaces" else fixture["focus"][scenario["focus"]]
        record = {"id": scenario["id"], "cardinality": cardinality, "samples": [], "failures": []}
        output["scenarios"].append(record)
        write_json_atomic(args.output, output)
        print(json.dumps({"scenario": scenario["id"], "phase": "collecting"}), flush=True)
        route_template = scenario["path"].format(**fixture["identities"])
        for size in policy["pageSizes"] if scenario["paged"] else [0]:
            remembered = {}
            for iteration in range(policy["samples"] + 1):
                cursor = None
                page_one = None
                for page in (1, 2) if scenario["paged"] else (1,):
                    query = {scenario["paginationParameter"]: size} if scenario["paged"] else {}
                    if scenario.get("cursor"):
                        if page == 2:
                            if cursor is None:
                                raise PerformanceContractError("missing message cursor")
                            query["before"] = cursor
                    elif scenario["paged"]:
                        query["page"] = page
                    route = route_template + (("&" if "?" in route_template else "?") + urllib.parse.urlencode(query) if query else "")
                    capture_id = uuid.uuid4().hex
                    # Iteration zero warms each exact route/size and is never measured.
                    measured_headers = headers | ({"X-Performance-Capture": capture_id} if iteration else {})
                    started = time.perf_counter()
                    status, payload = request(opener, base, route, measured_headers)
                    elapsed_ms = (time.perf_counter() - started) * 1000
                    if status != 200:
                        raise PerformanceContractError(f"scenario {scenario['id']} failed with HTTP {status}")
                    identities, cursor = page_identity(payload, scenario)
                    if scenario["paged"] and len(identities) != min(size, max(0, cardinality - (page - 1) * size)):
                        record["failures"].append("response-page-cardinality")
                    if page == 1:
                        page_one = identities
                    elif set(page_one or []) & set(identities):
                        record["failures"].append("overlapping-pages")
                    if page in remembered and remembered[page] != identities:
                        record["failures"].append("unstable-page-order")
                    remembered[page] = identities
                    if not iteration:
                        continue
                    path = capture_dir / f"{capture_id}.json"
                    # Server writes after the response pipeline; allow that bounded race.
                    deadline = time.monotonic() + 5
                    while not path.exists() and time.monotonic() < deadline:
                        time.sleep(0.01)
                    if not path.exists():
                        raise PerformanceContractError("request capture file missing")
                    try:
                        capture = validate_capture(load_json(path))
                    finally:
                        path.unlink(missing_ok=True)
                    if capture["status"] != status:
                        raise PerformanceContractError("capture HTTP status mismatch")
                    failures = capture_failures(capture, scenario, size, policy)
                    record["failures"].extend(failures)
                    record["samples"].append({"pageSize": size, "page": page, "iteration": iteration, "returnedCount": len(identities), "requestDurationMs": elapsed_ms, "dbTimeFraction": capture["totalDurationMs"] / elapsed_ms, "capture": capture, "fingerprintCounts": fingerprint_counts(capture)})
                    write_json_atomic(args.output, output)
            samples = [s["capture"]["totalDurationMs"] for s in record["samples"] if s["pageSize"] == size and s["page"] == 1]
            output["measurements"].append({"schemaVersion": 1, "scenario": scenario["id"], "metric": "db.total_time_ms", "unit": "ms", "headSha": output["headSha"], "samples": samples, "attempt": 1, "pageSize": size, "measurementEnvelope": {"warmupSamplesExcluded": True, "environmentStable": True, "benchmarkExitCode": 0, "timedOut": False}})
        record["failures"] = sorted(set(record["failures"]))
        write_json_atomic(args.output, output)
    for check in contract["planChecks"]:
        if check["profile"] == args.profile:
            project = os.environ.get("COGLATAS_PERFORMANCE_COMPOSE_PROJECT")
            command = ["psql", "-h", "127.0.0.1", "-U", "coglatas_performance", "-d", "coglatas_performance"]
            if project:
                command = ["docker", "compose", "-p", project, "-f", str(repository_root() / "infra/compose/performance/environment.yml"), "exec", "-T", "postgres", "psql", "-U", "coglatas_performance", "-d", "coglatas_performance"]
            output["plans"].append(plan_check(check, fixture, command))
    output["collectionComplete"] = True
    return output


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", choices=("small", "medium"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        root = repository_root()
        contract = load_json(root / "performance/db-scenarios.json")
        validate_contract(contract, load_json(root / "performance/scenarios.json"))
        output = collect(args, contract)
        write_json_atomic(args.output, output)
        # Aggregation is the single blocking boundary, after both datasets exist.
        print(json.dumps({"profile": args.profile, "scenarios": len(output["scenarios"]), "structuralFailures": sum(bool(s["failures"]) for s in output["scenarios"])}, sort_keys=True))
        return 0
    except (PerformanceContractError, OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        # Incoming exception text may contain SQL/connection strings/error bodies.
        safe_messages = {
            "instrumentation empty or inconsistent", "unsafe or invalid capture fields", "unsafe command fields",
            "invalid SQL identity", "unsafe table identity", "inconsistent DB duration", "inconsistent slow command evidence",
            "invalid slow command evidence", "DB probe response is not a collection", "DB probe response has no items",
            "invalid or duplicate response identity", "capture HTTP status mismatch", "fixture/fingerprint mismatch",
            "missing head identity", "DB probe CSRF bootstrap failed", "DB probe login failed", "missing message cursor",
            "selected EXPLAIN command failed", "selected plan fixture is too small", "request capture file missing"
        }
        message = str(error)
        reason = message if message in safe_messages else type(error).__name__
        if re.fullmatch(r"scenario [a-z.\-]+ failed with HTTP [0-9]{3}", message):
            reason = message
        elif re.fullmatch(r"invalid (numeric|integer) (commandCount|totalDurationMs|durationMs|readOperations)", message):
            reason = message
        elif error.__cause__ is not None:
            reason = "evidence-read-" + type(error.__cause__).__name__
        print(f"PERF-05 collection failed: {reason}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
