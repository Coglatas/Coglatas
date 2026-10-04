from __future__ import annotations

import copy
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/performance"))
from common import PerformanceContractError, load_json
from db_gate import capture_failures, growth_failures, plan_invariant, validate_capture, validate_contract
from test_performance_comparator import fingerprint, measurement, approved_baseline

spec = importlib.util.spec_from_file_location("perf05_compare", ROOT / "scripts/performance/db-compare.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def capture(count=4, *, duration=2, rows=6, bounded=True):
    commands = [{"fingerprint": "a" * 64, "durationMs": duration, "readOperations": rows, "failed": False, "rootTable": "task_items", "bounded": bounded, "ordered": True} for _ in range(count)]
    return {"schemaVersion": 1, "status": 200, "commandCount": count, "totalDurationMs": count * duration, "commands": commands, "slowestCommands": commands[:5]}


class PerformanceDbGateTests(unittest.TestCase):
    def setUp(self):
        self.contract = load_json(ROOT / "performance/db-scenarios.json")
        self.policy = self.contract["policy"]
        self.scenario = next(s for s in self.contract["scenarios"] if s["id"] == "task.my-tasks")

    def test_complete_major_list_inventory_is_source_backed(self):
        validate_contract(self.contract, load_json(ROOT / "performance/scenarios.json"))
        bad = copy.deepcopy(self.contract)
        bad["scenarios"].pop()
        with self.assertRaises(PerformanceContractError):
            validate_contract(bad, load_json(ROOT / "performance/scenarios.json"))

    def test_disabled_budgets_are_rejected(self):
        for key, value in (("queryCountHardCeiling", 100000), ("slowCommandHardCeilingMs", float("inf")), ("maximumFixedPageQueryGrowth", 100)):
            bad = copy.deepcopy(self.contract)
            bad["policy"][key] = value
            with self.assertRaises(PerformanceContractError):
                validate_contract(bad, load_json(ROOT / "performance/scenarios.json"))

    def profile(self, cardinality, counts):
        return {"cardinality": cardinality, "samples": [{"pageSize": size, "page": 1, "capture": capture(counts[size])} for size in (5, 10) for _ in range(5)]}

    def test_fixed_batched_query_passes(self):
        self.assertEqual([], growth_failures(self.profile(60, {5: 4, 10: 4}), self.profile(260, {5: 4, 10: 4}), self.policy))
        self.assertEqual([], capture_failures(capture(), self.scenario, 5, self.policy))

    def test_controlled_n_plus_one_entity_growth_fails(self):
        self.assertIn("n-plus-one-cardinality-growth", growth_failures(self.profile(60, {5: 60, 10: 60}), self.profile(260, {5: 260, 10: 260}), self.policy))

    def test_page_size_n_plus_one_fails_even_at_fixed_cardinality(self):
        self.assertIn("n-plus-one-page-size-growth", growth_failures(self.profile(60, {5: 8, 10: 13}), self.profile(260, {5: 8, 10: 13}), self.policy))

    def test_page_size_change_cannot_hide_full_materialization(self):
        self.assertIn("over-materialized-page", capture_failures(capture(rows=61), self.scenario, 5, self.policy))
        self.assertIn("unbounded-collection-materialization", capture_failures(capture(rows=61, bounded=False), self.scenario, 10, self.policy))
        self.assertIn("missing-ordered-db-page", capture_failures(capture(bounded=False), self.scenario, 5, self.policy))

    def test_extreme_slow_query_blocks_but_small_timing_delta_does_not(self):
        self.assertEqual([], capture_failures(capture(duration=100), self.scenario, 5, self.policy))
        self.assertIn("extreme-slow-command", capture_failures(capture(duration=10001), self.scenario, 5, self.policy))
        self.assertIn("query-count-hard-ceiling", capture_failures(capture(count=81), self.scenario, 5, self.policy))

    def test_sensitive_parameter_body_and_unrecognized_fields_are_rejected(self):
        for field in ("parameters", "sql", "password", "body", "error", "connectionString"):
            unsafe = capture()
            unsafe["commands"][0][field] = "protected-content"
            with self.assertRaises(PerformanceContractError):
                validate_capture(unsafe)
        unsafe = capture()
        unsafe["commands"][0]["fingerprint"] = "SELECT @secret = 'protected'"
        with self.assertRaises(PerformanceContractError):
            validate_capture(unsafe)

    def test_empty_missing_and_inconsistent_instrumentation_fail(self):
        for invalid in (capture(count=0), capture(count=-1), capture(duration=float("nan"))):
            with self.assertRaises(PerformanceContractError):
                validate_capture(invalid)
        invalid = capture()
        invalid["totalDurationMs"] += 1
        with self.assertRaises(PerformanceContractError):
            validate_capture(invalid)

    def test_selected_plan_semantics_ignore_costs_minor_versions_and_unrelated_seq_scan(self):
        index = {"Node Type": "Index Scan", "Relation Name": "task_items", "Index Name": "PK_task_items", "Index Cond": '("Id" = protected)', "Total Cost": 99, "Filter": "protected"}
        plan = [{"Plan": {"Node Type": "Nested Loop", "Plans": [index, {"Node Type": "Seq Scan", "Relation Name": "small_table"}]}}]
        self.assertTrue(plan_invariant(plan, "task_items", "Id"))
        index["Total Cost"] = 999
        self.assertTrue(plan_invariant(plan, "task_items", "Id"))
        index["Index Name"] = "equivalent_index"
        self.assertTrue(plan_invariant(plan, "task_items", "Id"))
        index["Index Cond"] = '("TenantId" = protected)'
        self.assertFalse(plan_invariant(plan, "task_items", "Id"))
        bitmap = {"Node Type": "Bitmap Heap Scan", "Relation Name": "task_items", "Plans": [{"Node Type": "Bitmap Index Scan", "Index Name": "equivalent_index", "Index Cond": '("Id" = protected)'}]}
        self.assertTrue(plan_invariant(bitmap, "task_items", "Id"))
        self.assertFalse(plan_invariant([{"Plan": {"Node Type": "Seq Scan", "Relation Name": "task_items"}}], "task_items", "Id"))

    def test_high_baseline_outlier_cannot_hide_repeated_n_plus_one(self):
        small = self.profile(60, {5: 4, 10: 4})
        small["samples"][0]["capture"] = capture(50)
        failures = growth_failures(small, self.profile(260, {5: 14, 10: 14}), self.policy)
        self.assertIn("n-plus-one-cardinality-growth", failures)
        self.assertIn("unstable-query-count", failures)

    def test_unpaged_workspace_inventory_uses_cardinality_growth_without_fake_page_sizes(self):
        small = {"cardinality": 1, "samples": [{"pageSize": 0, "page": 1, "capture": capture(4)} for _ in range(5)]}
        medium = {"cardinality": 3, "samples": [{"pageSize": 0, "page": 1, "capture": capture(4)} for _ in range(5)]}
        self.assertEqual([], growth_failures(small, medium, self.policy))
        medium["samples"] = [{"pageSize": 0, "page": 1, "capture": capture(8)} for _ in range(5)]
        self.assertIn("n-plus-one-cardinality-growth", growth_failures(small, medium, self.policy))

    def test_aggregate_rejects_partial_or_forged_fixture_evidence(self):
        with self.assertRaises(PerformanceContractError):
            module.evaluate({"collectionComplete": False}, {"collectionComplete": True}, self.contract)

    def test_missing_growth_measurements_fail_closed(self):
        small = self.profile(60, {5: 4, 10: 4})
        medium = self.profile(260, {5: 4, 10: 4})
        medium["samples"].pop()
        with self.assertRaises(PerformanceContractError):
            growth_failures(small, medium, self.policy)

    def test_duration_adapter_compares_repeated_samples_with_approved_main_baseline(self):
        fp = fingerprint()
        stream = measurement([10, 10, 10, 10, 10], metric="db.total_time_ms")
        stream["pageSize"] = 0
        profile = {"headSha": stream["headSha"], "fixtureHash": fp["fixture"]["hash"], "profile": "medium", "measurements": [stream]}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "medium").mkdir()
            (root / "medium/workspace.list.json").write_text(json.dumps(approved_baseline([8, 8, 8, 8, 8], metric="db.total_time_ms")))
            results = module.duration_results(profile, fp, ROOT, root)
        self.assertEqual("trend-recorded", results[0]["reasonCode"])
        self.assertEqual(5, results[0]["sampleCount"])
        self.assertEqual(2, results[0]["absoluteDelta"])

    def test_duration_adapter_keeps_missing_baseline_invalid_and_page_sizes_separate(self):
        fp = fingerprint()
        stream = measurement([10, 10, 10, 10, 10], metric="db.total_time_ms")
        stream["pageSize"] = 5
        other = copy.deepcopy(stream)
        other["pageSize"] = 10
        other["samples"] = [999] * 5
        profile = {"headSha": stream["headSha"], "fixtureHash": fp["fixture"]["hash"], "profile": "medium", "measurements": [stream, other]}
        results = module.duration_results(profile, fp, ROOT)
        self.assertEqual(1, len(results))
        self.assertEqual("invalid", results[0]["decision"])
        self.assertEqual(5, results[0]["sampleCount"])


if __name__ == "__main__":
    unittest.main()
