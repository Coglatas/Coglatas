"""Reject false-green or changed packet test evidence."""
import collections
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("packet_verify", Path(__file__).with_name("verify.py"))
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)

class EvidenceTests(unittest.TestCase):
    def trx(self, total=1, executed=1, passed=1, outcome="Passed", name="Test.A", extra=""):
        return (f'<TestRun xmlns="{verify.NAMESPACE["t"]}"><Results>'
                f'<UnitTestResult testName="{name}" outcome="{outcome}"/>'
                '<UnitTestResult testName="Test.B" outcome="Passed"/>'
                f'</Results><ResultSummary outcome="Completed"><Counters total="{total}" '
                f'executed="{executed}" passed="{passed}" {extra}/></ResultSummary></TestRun>')

    def parse(self, text):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "run.trx"
            path.write_text(text)
            return verify.trx_inventory(path)

    def test_valid_inventory(self):
        self.assertEqual(self.parse(self.trx(2, 2, 2)), collections.Counter({"Test.A": 1, "Test.B": 1}))

    def test_rejects_zero_failed_skipped_and_incomplete_counts(self):
        for total, executed, passed, extra in [(0, 0, 0, ""), (2, 1, 1, ""),
                                               (2, 2, 1, ""), (2, 2, 2, 'notExecuted="1"'),
                                               (2, 2, 2, 'failed="1"')]:
            with self.subTest(total=total, executed=executed, passed=passed, extra=extra):
                with self.assertRaises(ValueError):
                    self.parse(self.trx(total, executed, passed, extra=extra))

    def test_rejects_result_counter_disagreement(self):
        with self.assertRaises(ValueError):
            self.parse(self.trx())

    def test_rejects_nonpassing_result_even_with_passing_counters(self):
        with self.assertRaises(ValueError):
            self.parse(self.trx(2, 2, 2, outcome="NotExecuted"))

    def test_rejects_missing_identity(self):
        with self.assertRaises(ValueError):
            self.parse(self.trx(2, 2, 2, name=""))

    def test_rejects_disappearing_or_duplicate_test_multiplicity(self):
        before = {"backend": collections.Counter({"Test.A": 1, "Test.B": 1})}
        for after in [{"backend": collections.Counter({"Test.A": 1})},
                      {"backend": collections.Counter({"Test.A": 2, "Test.B": 1})}]:
            with self.assertRaises(ValueError):
                verify.require_same(before, after)

    def test_rejects_malformed_or_missing_summary(self):
        for text in ["<TestRun/>", "<"]:
            with self.assertRaises((ValueError, verify.ET.ParseError)):
                self.parse(text)

    def test_amendment_preserves_historical_plan_and_static_receiver(self):
        payload = json.loads(Path(__file__).with_name("payload.json").read_text())
        historical = json.dumps(payload["plan"], sort_keys=True)
        for packet_id, expected in [("P02", 6), ("P04", 13)]:
            amended = verify.amended_plan(payload["plan"], packet_id)
            packet = next(p for p in amended["packets"] if p["id"] == packet_id)
            ops = [op for file in packet["files"] for op in file["operations"]
                   if op["new"].startswith("Enumerable.Contains(")]
            self.assertEqual(sum(op["count"] for op in ops), expected)
            self.assertTrue(all("<Guid>" not in op["new"] for op in ops))
        self.assertEqual(json.dumps(payload["plan"], sort_keys=True), historical)

    def test_packet_selection_routes_independent_files(self):
        with tempfile.TemporaryDirectory() as root:
            config = Path(root)
            (config / "selections").mkdir()
            for packet_id in ["P02", "P04"]:
                selection = {"packet": packet_id, "baseline_sha": "a" * 40, "stage": "verify"}
                (config / "selections" / f"{packet_id}.json").write_text(json.dumps(selection))
                self.assertEqual(verify.read_selection(config, f"qodana/packet-{packet_id.lower()}-proof"), selection)

    def test_packet_selection_preserves_legacy_matching_packet(self):
        with tempfile.TemporaryDirectory() as root:
            config = Path(root)
            selection = {"packet": "P01", "baseline_sha": "a" * 40, "stage": "verify"}
            (config / "selection.json").write_text(json.dumps(selection))
            self.assertEqual(verify.read_selection(config, "qodana/packet-p01-source"), selection)
            with self.assertRaises(ValueError):
                verify.read_selection(config, "qodana/packet-p02-proof")

    def test_packet_selection_rejects_cross_packet_file(self):
        with tempfile.TemporaryDirectory() as root:
            config = Path(root)
            (config / "selections").mkdir()
            (config / "selections/P02.json").write_text(json.dumps({"packet": "P04"}))
            with self.assertRaises(ValueError):
                verify.read_selection(config, "qodana/packet-p02-proof")

    def test_packet_selection_rejects_unsupported_or_unsafe_branch(self):
        for branch in ["main", "qodana/packet-p03-proof", "qodana/packet-../../P02", "qodana/packet-p02-proof/extra"]:
            with self.subTest(branch=branch):
                with self.assertRaises(ValueError):
                    verify.read_selection(Path("unused"), branch)

if __name__ == "__main__":
    unittest.main()
