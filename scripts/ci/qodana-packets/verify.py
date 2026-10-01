#!/usr/bin/env python3
"""Verify one approved #976 packet against unchanged source and real PostgreSQL."""
from __future__ import annotations
import collections
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

HELPER_SHA256 = "305c7f54d04ea3b83ddcc807c7b462a5a194a4ecebdc2ffb5c814626d7415a15"
PLAN_SHA256 = "c129f5f0e6a89fef4ef64cb1b52f2ab67b62dfd295244976a10bdb9256447424"
ALLOWED_PACKETS = {"P01", "P02", "P04", "P11", "P13"}
NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def checked(args: list[str], cwd: Path, capture: bool = False) -> str:
    result = subprocess.run(args, cwd=cwd, check=True, text=True,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout or ""

def is_ancestor(repo: Path, before: str, after: str) -> bool:
    result = subprocess.run(["git", "merge-base", "--is-ancestor", before, after], cwd=repo)
    if result.returncode not in {0, 1}:
        raise ValueError("Cannot establish revision ancestry")
    return result.returncode == 0

def integrated_pr_base(repo: Path, head: str, base: str) -> str | None:
    for sha in [head, base]:
        if not re.fullmatch(r"[0-9a-f]{40}", sha):
            raise ValueError("Require immutable event head/base revisions")
    if checked(["git", "rev-parse", "HEAD"], repo, capture=True).strip() != head:
        raise ValueError("Checkout differs from event head")
    return base if is_ancestor(repo, base, head) else None

def resolve_source_baseline(repo: Path, configured: str, head: str, base: str) -> str:
    integrated = integrated_pr_base(repo, head, base)
    if integrated and is_ancestor(repo, configured, integrated):
        return integrated
    return configured

def trx_inventory(path: Path) -> collections.Counter:
    root = ET.parse(path).getroot()
    summary = root.find("t:ResultSummary", NAMESPACE)
    if summary is None or summary.get("outcome") not in {"Completed", "Passed"}:
        raise ValueError(f"Incomplete TRX: {path}")
    counters = summary.find("t:Counters", NAMESPACE)
    if counters is None:
        raise ValueError(f"Missing TRX counters: {path}")
    counts = {key: int(value) for key, value in counters.attrib.items()}
    total = counts.get("total", 0)
    if total < 1 or counts.get("executed") != total or counts.get("passed") != total:
        raise ValueError(f"Zero, failed or unexecuted selection: {path}")
    if any(value for key, value in counts.items() if key not in {"total", "executed", "passed"}):
        raise ValueError(f"Non-passing TRX counters: {path}")
    results = root.findall("t:Results/t:UnitTestResult", NAMESPACE)
    if len(results) != total or any(r.get("outcome") != "Passed" for r in results):
        raise ValueError(f"TRX results/counters disagree: {path}")
    if any(not r.get("testName") for r in results):
        raise ValueError(f"Missing test identity: {path}")
    return collections.Counter(r.attrib["testName"] for r in results)

def require_same(before: dict[str, collections.Counter], after: dict[str, collections.Counter]) -> None:
    if before != after:
        raise ValueError("Baseline/candidate test identities or multiplicities changed")

def read_selection(config_dir: Path, head_ref: str) -> dict:
    match = re.fullmatch(r"qodana/packet-(p[0-9]{2})-(?:proof|source)", head_ref)
    if match is None or match[1].upper() not in ALLOWED_PACKETS:
        raise ValueError("Unsupported packet branch")
    packet_id = match[1].upper()
    path = config_dir / "selections" / f"{packet_id}.json"
    if not path.is_file():
        path = config_dir / "selection.json"
    selection = json.loads(path.read_text(encoding="utf-8"))
    if selection.get("packet") != packet_id:
        raise ValueError("Selection does not match the PR branch packet")
    return selection

def amended_plan(original: dict, packet_id: str) -> dict:
    plan = copy.deepcopy(original)
    if packet_id in {"P02", "P04"}:
        packet = next(p for p in plan["packets"] if p["id"] == packet_id)
        changes = 0
        for entry in packet["files"]:
            for op in entry["operations"]:
                if op["kind"] == "replace" and op["new"].startswith("Enumerable.Contains<Guid>("):
                    op["new"] = op["new"].replace("Enumerable.Contains<Guid>(", "Enumerable.Contains(", 1)
                    changes += op["count"]
        if changes != {"P02": 6, "P04": 13}[packet_id]:
            raise ValueError("Approved Contains amendment count drift")
    return plan

def run_tests(repo: Path, evidence: Path, packet: dict) -> dict[str, collections.Counter]:
    evidence.mkdir(parents=True, exist_ok=False)
    checked(["dotnet", "restore", "Coglatas.slnx", "--disable-parallel"], repo)
    checked(["dotnet", "build", "Coglatas.slnx", "-c", "Release", "--no-restore",
             "--no-incremental", "--disable-build-servers", "-m:1"], repo)
    checked(["dotnet", "tool", "restore"], repo)
    options = ["--project", "src/Coglatas.Infrastructure", "--startup-project",
               "src/Coglatas.Web", "--configuration", "Release", "--no-build"]
    checked(["dotnet", "ef", "database", "update"] + options, repo)
    checked(["dotnet", "ef", "migrations", "has-pending-model-changes"] + options, repo)
    selections = [
        ("focused", "tests/Coglatas.Tests/Coglatas.Tests.csproj", packet["test_filter"]),
        ("backend", "tests/Coglatas.Tests/Coglatas.Tests.csproj", None),
        ("architecture", "tests/Coglatas.Architecture.Tests/Coglatas.Architecture.Tests.csproj", None),
    ]
    inventories = {}
    for label, project, test_filter in selections:
        command = ["dotnet", "test", project, "-c", "Release", "--no-build",
                   "--disable-build-servers", "-m:1"]
        if test_filter:
            command += ["--filter", test_filter]
        discovery = checked(command + ["--list-tests"], repo, capture=True)
        (evidence / f"{label}-discovery.txt").write_text(discovery, encoding="utf-8")
        trx = evidence / f"{label}.trx"
        checked(command + ["--logger", f"trx;LogFileName={trx.name}",
                           "--results-directory", str(evidence)], repo)
        inventories[label] = trx_inventory(trx)
        print(f"{label}: {sum(inventories[label].values())} passed; zero failed/skipped", flush=True)
    provider_names = [name for name in inventories["backend"] if "PostgreSql" in name]
    if not provider_names:
        raise ValueError("No executed PostgreSQL test identities in full backend TRX")
    inventories["postgresql"] = collections.Counter({
        name: inventories["backend"][name] for name in provider_names
    })
    (evidence / "inventory.json").write_text(json.dumps(inventories, indent=2, sort_keys=True) + "\n")
    return inventories

def main() -> None:
    source = Path(os.environ["GITHUB_WORKSPACE"]).resolve()
    temp = Path(os.environ["RUNNER_TEMP"]).resolve() / "qodana-packet-proof"
    temp.mkdir(exist_ok=False)
    evidence = source / "artifacts/qodana-packet-proof"
    evidence.mkdir(parents=True, exist_ok=True)
    config_dir = source / "scripts/ci/qodana-packets"
    selection = read_selection(config_dir, os.environ["PACKET_BRANCH"])
    if set(selection) != {"packet", "baseline_sha", "stage"}:
        raise ValueError("Unexpected selection fields")
    packet_id, baseline_sha, stage = (selection[k] for k in ("packet", "baseline_sha", "stage"))
    if packet_id not in ALLOWED_PACKETS or stage not in {"prepare", "verify"}:
        raise ValueError("Packet/stage has not been approved for this runner")
    if not re.fullmatch(r"[0-9a-f]{40}", baseline_sha):
        raise ValueError("Baseline must be an immutable full commit SHA")
    head_sha = os.environ["PACKET_HEAD_SHA"]
    if not re.fullmatch(r"[0-9a-f]{40}", head_sha):
        raise ValueError("Head must be an immutable full commit SHA")
    checked(["git", "merge-base", "--is-ancestor", baseline_sha, head_sha], source)
    configured_baseline_sha = baseline_sha
    event_base_sha = os.environ["PACKET_BASE_SHA"]
    baseline_sha = resolve_source_baseline(source, baseline_sha, head_sha, event_base_sha)
    if checked(["dotnet", "--version"], source, capture=True).strip() != "10.0.401":
        raise ValueError("SDK drift: require 10.0.401")
    if not os.environ.get("POSTGRES_TEST_CONNECTION_STRING", "").strip():
        raise ValueError("POSTGRES_TEST_CONNECTION_STRING is required; no fallback")
    # Connection is demonstrated independently; only the server version is printed.
    postgres_version = checked(["psql", "-XAt", "-h", "127.0.0.1", "-U", "coglatas_ci",
                                "-d", "coglatas_ci", "-c", "SHOW server_version;"],
                               source, capture=True).strip()
    if not postgres_version.startswith("18."):
        raise ValueError("PostgreSQL drift: require version 18")
    payload = json.loads((config_dir / "payload.json").read_text())
    helper = payload["helper"].encode("utf-8")
    canonical_plan = json.dumps(payload["plan"], sort_keys=True, separators=(",", ":")).encode()
    if sha256(helper) != HELPER_SHA256 or sha256(canonical_plan) != PLAN_SHA256:
        raise ValueError("Canonical helper/plan integrity mismatch")
    plan = amended_plan(payload["plan"], packet_id)
    packet = next(p for p in plan["packets"] if p["id"] == packet_id)
    if packet["new_tests"]:
        raise ValueError("Fixed test payload support requires a separate reviewed extension")
    source_paths = {entry["path"] for entry in packet["files"]}
    changed = checked(["git", "diff", "--name-only", baseline_sha, head_sha], source, capture=True).splitlines()
    auxiliary = {".github/workflows/qodana-packet-verification.yml", "docs/ci/qodana-packet-verification.md"}
    for path in changed:
        if path not in source_paths and path not in auxiliary and not path.startswith("scripts/ci/qodana-packets/"):
            raise ValueError(f"Out-of-packet source/dependency/test drift: {path}")
    if stage == "prepare" and any(path in source_paths for path in changed):
        raise ValueError("Prepare requires unchanged production source")
    helper_path, plan_path = temp / "apply_plan.py", temp / "execution-plan.json"
    helper_path.write_bytes(helper)
    plan_path.write_text(json.dumps(plan, indent=2) + "\n")
    baseline_repo = temp / "baseline"
    checked(["git", "worktree", "add", "--detach", str(baseline_repo), baseline_sha], source)
    helper_command = [sys.executable, str(helper_path), "--repo", str(baseline_repo),
                      "--plan", str(plan_path), "--packet", packet_id]
    # Fail closed on preimages before spending time on tests.
    checked(helper_command + ["--check"], baseline_repo)
    print(f"BASELINE {baseline_sha} packet={packet_id} PostgreSQL={postgres_version}", flush=True)
    baseline = run_tests(baseline_repo, evidence / "baseline", packet)
    # Application occurs only after the unchanged-source focused/full baseline passes.
    checked(helper_command + ["--check"], baseline_repo)
    checked(helper_command + ["--apply"], baseline_repo)
    patch = checked(["git", "diff", "--binary"], baseline_repo, capture=True)
    (evidence / "canonical.patch").write_text(patch, encoding="utf-8")
    hashes = {path: sha256((baseline_repo / path).read_bytes()) for path in sorted(source_paths)}
    candidate_repo = baseline_repo
    if stage == "verify":
        candidate_repo = temp / "candidate"
        checked(["git", "worktree", "add", "--detach", str(candidate_repo), head_sha], source)
        for path in source_paths:
            if (candidate_repo / path).read_bytes() != (baseline_repo / path).read_bytes():
                raise ValueError(f"Head differs from canonical approved result: {path}")
    print(f"CANDIDATE stage={stage} head={head_sha} source_sha256={json.dumps(hashes, sort_keys=True)}", flush=True)
    candidate = run_tests(candidate_repo, evidence / "candidate", packet)
    require_same(baseline, candidate)
    proof = {
        "packet": packet_id, "baseline_sha": baseline_sha, "head_sha": head_sha,
        "configured_baseline_sha": configured_baseline_sha, "event_base_sha": event_base_sha,
        "stage": stage, "candidate_is_committed_head": stage == "verify",
        "sdk": "10.0.401", "postgresql": postgres_version,
        "canonical_helper_sha256": HELPER_SHA256, "historical_plan_sha256": PLAN_SHA256,
        "derived_plan_sha256": sha256(plan_path.read_bytes()), "source_sha256": hashes,
        "test_counts": {key: sum(value.values()) for key, value in candidate.items()},
        "evidence_sha256": {str(path.relative_to(evidence)): sha256(path.read_bytes())
                            for path in evidence.rglob("*") if path.is_file()},
    }
    (evidence / "proof.json").write_text(json.dumps(proof, indent=2, sort_keys=True) + "\n")
    print("PACKET_PROOF " + json.dumps(proof, sort_keys=True), flush=True)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write("### Qodana packet verification\n\n")
            output.write(f"Packet {packet_id}; stage {stage}; baseline {baseline_sha}; head {head_sha}.\n\n")
            output.write("Same baseline/candidate test identities and multiplicities; zero failed/skipped. "
                         "Prepare evidence does not claim a committed production change.\n")

if __name__ == "__main__":
    main()
