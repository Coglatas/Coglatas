#!/usr/bin/env python3
"""Run fixed P11/P13 tests before production edits; baseline evidence only."""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import verify

HELPER_SHA256 = "634ef30bd5c20490996650c819178bbd37212047c36d43092d3c44306ef4cf77"
PLAN_SHA256 = "baf01161e4913e084f79a79f199b10bbebcf13940ac522d3c4a36374c3824d4e"
FIXED_TESTS = {
    "P11": ("tests/Coglatas.Tests/Quality/QodanaFixedWireNameTests.cs", "c86474aad57b19211c96f9be18bc8621408d56527d062fa5a4d815d8c7ec4187"),
    "P13": ("tests/Coglatas.Tests/Quality/QodanaFixedExceptionContractTests.cs", "5bc14e1255c56f4efe1d6380f2f8da658235b273771273e1ac8b9cae981aa577"),
}
OVERLAPS = {
    "P11": {
        "src/Coglatas.Application/StudentRecords/StudentRecordService.cs": "P06",
        "src/Coglatas.Infrastructure/Persistence/AuditPackageExportService.cs": "P04",
    },
    "P13": {
        "src/Coglatas.Application/Notifications/TaskDeadlineDigestServices.cs": "P07",
    },
}

def load_payload(config: Path) -> tuple[dict, dict, dict]:
    payload = json.loads((config / "r2-payload.json").read_text(encoding="utf-8"))
    historical = json.loads((config / "payload.json").read_text(encoding="utf-8"))
    for data, helper_hash, plan_hash in [
        (payload, HELPER_SHA256, PLAN_SHA256),
        (historical, verify.HELPER_SHA256, verify.PLAN_SHA256),
    ]:
        if verify.sha256(data["helper"].encode()) != helper_hash:
            raise ValueError("Immutable helper integrity mismatch")
        plan = json.dumps(data["plan"], sort_keys=True, separators=(",", ":")).encode()
        if verify.sha256(plan) != plan_hash:
            raise ValueError("Immutable plan integrity mismatch")
    if set(payload["tests"]) != set(FIXED_TESTS):
        raise ValueError("Unexpected fixed test payloads")
    for packet_id, (path, digest) in FIXED_TESTS.items():
        test = payload["tests"][packet_id]
        if test["path"] != path or test["sha256"] != digest or verify.sha256(test["content"].encode()) != digest:
            raise ValueError("Fixed test payload drift")
    namespace = {"__name__": "immutable_original_helper"}
    exec(compile(historical["helper"], "immutable_apply_plan.py", "exec"), namespace)
    return payload, historical, namespace

def upstream_variant(packet_id: str, path: str, audited: bytes, current: bytes,
                     historical: dict, namespace: dict) -> str | None:
    if current == audited:
        return None
    previous_id = OVERLAPS[packet_id].get(path)
    if previous_id:
        plan = verify.amended_plan(historical["plan"], previous_id)
        packet = next(p for p in plan["packets"] if p["id"] == previous_id)
        entry = next(f for f in packet["files"] if f["path"] == path)
        if current == namespace["transform_bytes"](audited, entry["operations"]):
            return previous_id
    raise ValueError(f"STOP: source differs from audited or exact known upstream packet: {path}")

def require_test(repo: Path, packet_id: str) -> None:
    path, digest = FIXED_TESTS[packet_id]
    target = repo / path
    if target.is_symlink() or verify.sha256(target.read_bytes()) != digest:
        raise ValueError("Fixed tests missing or modified")

def require_baseline_paths(changed: list[str], packet_id: str, integrated: bool) -> None:
    allowed = {".github/workflows/qodana-packet-verification.yml", "docs/ci/qodana-packet-verification.md"}
    if integrated:
        allowed.add(FIXED_TESTS[packet_id][0])
    for path in changed:
        if path not in allowed and not path.startswith("scripts/ci/qodana-packets/"):
            raise ValueError(f"R2 baseline has production/dependency/other-test changes: {path}")

def main() -> None:
    source = Path(os.environ["GITHUB_WORKSPACE"]).resolve()
    config = source / "scripts/ci/qodana-packets"
    selection = verify.read_selection(config, os.environ["PACKET_BRANCH"])
    if set(selection) != {"packet", "baseline_sha", "audit_test_sha", "stage"}:
        raise ValueError("Unexpected R2 selection fields")
    packet_id = selection["packet"]
    if packet_id not in FIXED_TESTS or selection["stage"] != "baseline":
        raise ValueError("Only unchanged-production R2 baselines are supported")
    head_sha = os.environ["PACKET_HEAD_SHA"]
    for sha in [head_sha, selection["baseline_sha"], selection["audit_test_sha"]]:
        if not re.fullmatch(r"[0-9a-f]{40}", sha):
            raise ValueError("Require immutable full revisions")
        verify.checked(["git", "merge-base", "--is-ancestor", sha, head_sha], source)
    if verify.checked(["dotnet", "--version"], source, capture=True).strip() != "10.0.401":
        raise ValueError("SDK drift")
    if not os.environ.get("POSTGRES_TEST_CONNECTION_STRING", "").strip():
        raise ValueError("Real PostgreSQL connection required")
    version = verify.checked(["psql", "-XAt", "-h", "127.0.0.1", "-U", "coglatas_ci",
                              "-d", "coglatas_ci", "-c", "SHOW server_version;"], source, capture=True).strip()
    if not version.startswith("18."):
        raise ValueError("PostgreSQL version drift")
    payload, historical, namespace = load_payload(config)
    packet = next(p for p in payload["plan"]["packets"] if p["id"] == packet_id)
    event_base_sha = os.environ["PACKET_BASE_SHA"]
    integrated = verify.integrated_pr_base(source, head_sha, event_base_sha)
    # With main already integrated, head contains unchanged main production plus
    # the exact fixed test. Verify the entire diff before using head as baseline.
    comparison_sha = integrated or selection["baseline_sha"]
    baseline_sha = head_sha if integrated else selection["baseline_sha"]
    changed = verify.checked(["git", "diff", "--name-only", comparison_sha, head_sha],
                             source, capture=True).splitlines()
    require_baseline_paths(changed, packet_id, bool(integrated))
    temp = Path(os.environ["RUNNER_TEMP"]).resolve() / "qodana-packet-proof"
    temp.mkdir(exist_ok=False)
    evidence = source / "artifacts/qodana-packet-proof"
    evidence.mkdir(parents=True, exist_ok=True)
    audit, baseline = temp / "audit", temp / "baseline"
    for repo, sha in [(audit, selection["audit_test_sha"]), (baseline, baseline_sha)]:
        verify.checked(["git", "worktree", "add", "--detach", str(repo), sha], source)
        require_test(repo, packet_id)
    require_test(source, packet_id)
    helper, plan = temp / "apply_extension.py", temp / "extension-plan.json"
    helper.write_bytes(payload["helper"].encode())
    plan.write_text(json.dumps(payload["plan"], indent=2) + "\n", encoding="utf-8")
    # Dry run only in the isolated audited tests-first branch. Never amend hashes.
    verify.checked([sys.executable, str(helper), "--repo", str(audit), "--plan", str(plan),
                    "--packet", packet_id], audit)
    upstream, source_hashes = {}, {}
    for entry in packet["files"]:
        path = entry["path"]
        before, current = (audit / path).read_bytes(), (baseline / path).read_bytes()
        upstream[path] = upstream_variant(packet_id, path, before, current, historical, namespace)
        source_hashes[path] = verify.sha256(current)
        if (source / path).read_bytes() != current:
            raise ValueError("Head differs from unchanged production baseline")
    inventory = verify.run_tests(baseline, evidence / "baseline", packet)
    proof = {
        "packet": packet_id, "stage": "baseline", "candidate_is_committed_head": False,
        "production_change_applied": False, "head_sha": head_sha,
        "baseline_sha": baseline_sha, "audit_test_sha": selection["audit_test_sha"],
        "configured_baseline_sha": selection["baseline_sha"], "event_base_sha": event_base_sha,
        "production_comparison_sha": comparison_sha,
        "sdk": "10.0.401", "postgresql": version, "fixed_test_sha256": FIXED_TESTS[packet_id][1],
        "canonical_helper_sha256": HELPER_SHA256, "historical_plan_sha256": PLAN_SHA256,
        "known_upstream_packets": upstream, "baseline_source_sha256": source_hashes,
        "test_counts": {key: sum(value.values()) for key, value in inventory.items()},
        "evidence_sha256": {str(path.relative_to(evidence)): verify.sha256(path.read_bytes())
                            for path in evidence.rglob("*") if path.is_file()},
    }
    (evidence / "baseline-proof.json").write_text(json.dumps(proof, indent=2, sort_keys=True) + "\n")
    print("PACKET_BASELINE_PROOF " + json.dumps(proof, sort_keys=True), flush=True)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write(f"### Fixed packet baseline\n\n{packet_id}: unchanged production and fixed tests passed. "
                         "No production transformation, candidate acceptance or packet completion is claimed.\n")

if __name__ == "__main__":
    main()
