from __future__ import annotations

import copy
import importlib.util
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/performance"))
from common import PerformanceContractError, load_json
from db_gate import capture_failures, growth_failures, plan_invariant, validate_capture, validate_contract

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
        index = {"Node Type": "Index Scan", "Relation Name": "task_items", "Index Name": "PK_task_items", "Total Cost": 99, "Filter": "protected"}
        plan = [{"Plan": {"Node Type": "Nested Loop", "Plans": [index, {"Node Type": "Seq Scan", "Relation Name": "small_table"}]}}]
        self.assertTrue(plan_invariant(plan, "task_items", "PK_task_items"))
        index["Total Cost"] = 999
        self.assertTrue(plan_invariant(plan, "task_items", "PK_task_items"))
        index["Index Name"] = "wrong_index"
        self.assertFalse(plan_invariant(plan, "task_items", "PK_task_items"))
        bitmap = {"Node Type": "Bitmap Heap Scan", "Relation Name": "task_items", "Plans": [{"Node Type": "Bitmap Index Scan", "Index Name": "PK_task_items"}]}
        self.assertTrue(plan_invariant(bitmap, "task_items", "PK_task_items"))

    def test_missing_growth_measurements_fail_closed(self):
        small = self.profile(60, {5: 4, 10: 4})
        medium = self.profile(260, {5: 4, 10: 4})
        medium["samples"].pop()
        with self.assertRaises(PerformanceContractError):
            growth_failures(small, medium, self.policy)


if __name__ == "__main__":
    unittest.main()
