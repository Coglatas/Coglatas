from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/performance"))
from common import PerformanceContractError, FIXTURE_VERSION, fixture_hash, load_json
from test_performance_db import capture, module as comparison

spec = importlib.util.spec_from_file_location("performance_db_ci", ROOT / "scripts/performance/db-ci.py")
ci = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci)
HEAD = "a" * 40
BASE = "b" * 40


class PerformanceDbRoutingTests(unittest.TestCase):
    def git(self, paths=None, failure=False):
        rev = subprocess.CompletedProcess([], 0, stdout=HEAD + "\n")
        diff = subprocess.CalledProcessError(128, []) if failure else subprocess.CompletedProcess([], 0, stdout=b"\0".join(p.encode() for p in (paths or [])) + b"\0")
        return Mock(side_effect=[rev, diff])

    def test_only_explicit_unrelated_changes_are_not_applicable(self):
        result = ci.route(HEAD, BASE, "pull_request", self.git(["docs/ARCHITECTURE.md", "frontend/src/app/main.ts"]))
        self.assertFalse(result["required"])
        self.assertEqual("validated-not-applicable", result["reasonCode"])
        self.assertEqual(HEAD, result["headSha"])
        self.assertEqual(2, result["changedFileCount"])
        self.assertRegex(result["changedPathsHash"], "^[a-f0-9]{64}$")

    def test_backend_dependency_workflow_and_unknown_changes_run(self):
        for path in ("src/Coglatas.Web/Program.cs", "Directory.Packages.props", ".github/workflows/ci.yml", "new-feature/config.json", "performance/environment.json"):
            with self.subTest(path=path):
                self.assertTrue(ci.route(HEAD, BASE, "pull_request", self.git([path]))["required"])

    def test_missing_failed_or_empty_diff_runs_conservatively(self):
        for event, base in (("schedule", None), ("workflow_dispatch", None), ("push", "0" * 40), ("pull_request", None)):
            self.assertTrue(ci.route(HEAD, base, event, self.git())["required"])
        self.assertTrue(ci.route(HEAD, BASE, "pull_request", self.git([]))["required"])
        result = ci.route(HEAD, BASE, "pull_request", self.git(failure=True))
        self.assertEqual("conservative-diff-failure", result["reasonCode"])
        self.assertTrue(result["required"])

    def test_route_cannot_describe_a_different_checkout(self):
        git = Mock(return_value=subprocess.CompletedProcess([], 0, stdout=BASE))
        with self.assertRaises(PerformanceContractError):
            ci.route(HEAD, BASE, "pull_request", git)

    def test_applicable_missing_cancelled_skipped_or_failed_measurements_never_pass(self):
        route = {"schemaVersion": 1, "headSha": HEAD, "required": True, "reasonCode": "relevant-change"}
        for status in ("failure", "cancelled", "skipped", ""):
            with self.assertRaises(PerformanceContractError):
                ci.gate_decision(route, status, None, HEAD)
        for measurement in (None, {"schemaVersion": 1, "headSha": BASE, "decision": "pass"}):
            with self.assertRaises(PerformanceContractError):
                ci.gate_decision(route, "success", measurement, HEAD)
        self.assertEqual("pass", ci.gate_decision(route, "success", {"schemaVersion": 1, "headSha": HEAD, "decision": "pass"}, HEAD))
        for decision in ("regression", "invalid", "unstable", "insufficient-data"):
            self.assertEqual(decision, ci.gate_decision(route, "success", {"schemaVersion": 1, "headSha": HEAD, "decision": decision}, HEAD))

    def test_not_applicable_requires_route_evidence_and_skipped_collector(self):
        route = {"schemaVersion": 1, "headSha": HEAD, "required": False, "reasonCode": "validated-not-applicable"}
        self.assertEqual("not-applicable", ci.gate_decision(route, "skipped", None, HEAD))
        for route_change, status in (({"reasonCode": "unknown"}, "skipped"), ({"headSha": BASE}, "skipped"), ({}, "success")):
            with self.assertRaises(PerformanceContractError):
                ci.gate_decision(route | route_change, status, None, HEAD)

    def test_stable_aggregate_survives_doc_only_changes(self):
        workflow = (ROOT / ".github/workflows/performance-db.yml").read_text()
        triggers = workflow.split("permissions:", 1)[0]
        self.assertNotIn("paths:", triggers)
        self.assertIn("name: PostgreSQL query regression gate", workflow)
        self.assertIn("needs: [route, collect]", workflow)
        self.assertIn("--expected-sha", workflow)
        self.assertIn("Finalize exact-SHA aggregate evidence\n        if: always()", workflow)


class PerformanceDbBuildResolverTests(unittest.TestCase):
    def build_run(self):
        return {"id": 17, "head_sha": HEAD, "head_branch": "main", "event": "push", "status": "completed", "path": ".github/workflows/main-build-artifacts.yml"}

    def api(self, *, run_change=None, job_result="success", artifact_change=None):
        run = self.build_run() | (run_change or {})
        artifact = {"name": "main-build-artifacts", "expired": False, "workflow_run": {"head_sha": HEAD}} | (artifact_change or {})
        return Mock(side_effect=[{"workflow_runs": [run]}, {"jobs": [{"name": "Main runtime artifact assembler", "conclusion": job_result}]}, {"artifacts": [artifact]}])

    def test_exact_main_artifact_requires_its_successful_producer(self):
        self.assertEqual("17", ci.resolve_build("NYGsatoshi/Coglatas", HEAD, self.api()))
        for status in ("failure", "cancelled", "skipped"):
            with self.assertRaises(PerformanceContractError):
                ci.resolve_build("NYGsatoshi/Coglatas", HEAD, self.api(job_result=status))

    def test_pr_wrong_sha_expired_and_wrong_workflow_artifacts_are_rejected(self):
        for changes in ({"event": "pull_request"}, {"head_sha": BASE}, {"head_branch": "feature"}, {"path": ".github/workflows/ci.yml"}, {"status": "in_progress"}):
            with self.assertRaises(PerformanceContractError):
                ci.resolve_build("NYGsatoshi/Coglatas", HEAD, self.api(run_change=changes))
        for changes in ({"expired": True}, {"workflow_run": {"head_sha": BASE}}, {"name": "other"}):
            with self.assertRaises(PerformanceContractError):
                ci.resolve_build("NYGsatoshi/Coglatas", HEAD, self.api(artifact_change=changes))


class PerformanceDbComparisonIdentityTests(unittest.TestCase):
    def profile(self, name, contract):
        data = {"schemaVersion": 1, "fixtureVersion": FIXTURE_VERSION, "fixtureHash": fixture_hash(name),
                "profile": name, "headSha": HEAD, "collectionComplete": True, "warmupSamplesExcluded": True,
                "scenarios": [], "plans": []}
        for scenario in contract["scenarios"]:
            sizes = [5, 10] if scenario["paged"] else [0]
            samples = []
            for size in sizes:
                for page in ([1, 2] if scenario["paged"] else [1]):
                    for iteration in range(1, 6):
                        evidence = capture(rows=size + 1)
                        for command in evidence["commands"]:
                            command["rootTable"] = scenario["rootTable"]
                        evidence["slowestCommands"] = evidence["commands"][:5]
                        samples.append({"pageSize": size, "page": page, "iteration": iteration, "capture": evidence})
            data["scenarios"].append({"id": scenario["id"], "cardinality": 60 if name == "small" else 260, "failures": [], "samples": samples})
        if name == "medium":
            data["plans"] = [{"id": "task.primary-key-lookup", "tableRows": 3000, "requiredIndexPresent": True}]
        return data

    def test_same_wrong_sha_pair_cannot_satisfy_target(self):
        contract = load_json(ROOT / "performance/db-scenarios.json")
        small, medium = (self.profile(name, contract) for name in ("small", "medium"))
        self.assertEqual("pass", comparison.evaluate(small, medium, contract, HEAD)["decision"])
        with self.assertRaises(PerformanceContractError):
            comparison.evaluate(small, medium, contract, BASE)

    def test_missing_invalid_or_unstable_duration_cannot_turn_main_green(self):
        for results in ([], [{"decision": "invalid"}], [{"decision": "unstable"}], [{"decision": "insufficient-data"}], [{"decision": "regression"}]):
            self.assertNotEqual("pass", comparison.duration_decision(results))
        self.assertEqual("pass", comparison.duration_decision([{"decision": "pass"}]))

    def test_duration_inventory_rejects_missing_duplicate_and_wrong_page_streams(self):
        contract = load_json(ROOT / "performance/db-scenarios.json")
        measurements = [{"scenario": s["id"], "pageSize": 5 if s["paged"] else 0} for s in contract["scenarios"]]
        comparison.validate_duration_inventory({"measurements": measurements}, contract)
        for invalid in (measurements[:-1], measurements + [measurements[0]], measurements[:-1] + [measurements[-1] | {"pageSize": 10}]):
            with self.assertRaises(PerformanceContractError):
                comparison.validate_duration_inventory({"measurements": invalid}, contract)


if __name__ == "__main__":
    unittest.main()
