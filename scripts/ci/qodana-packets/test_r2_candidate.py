"""Reject canonical-candidate scope drift and conflicting upstream integration."""
from pathlib import Path
import tempfile
import unittest
import r2_verify

class R2CandidateTests(unittest.TestCase):
    def test_candidate_scope_permits_only_its_targets_and_auxiliary_files(self):
        packet = {"id": "P11", "files": [{"path": "src/allowed.cs"}]}
        r2_verify.require_candidate_paths(
            ["src/allowed.cs", "scripts/ci/qodana-packets/selections/P11.json"], packet)
        for path in ["src/other.cs", "frontend/package-lock.json", r2_verify.FIXED_TESTS["P13"][0]]:
            with self.subTest(path=path), self.assertRaises(ValueError):
                r2_verify.require_candidate_paths([path], packet)

    def test_normal_integration_preserves_disjoint_canonical_and_upstream_edits(self):
        audited = b"original upstream\nline 2\nline 3\nline 4\noriginal packet\n"
        upstream = audited.replace(b"original upstream", b"known upstream")
        canonical = audited.replace(b"original packet", b"canonical packet")
        with tempfile.TemporaryDirectory() as directory:
            result = r2_verify.merge_candidate_source(upstream, audited, canonical, Path(directory) / "merge")
        self.assertEqual(result, upstream.replace(b"original packet", b"canonical packet"))

    def test_conflict_stops_integration(self):
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(ValueError):
            r2_verify.merge_candidate_source(b"upstream\n", b"audited\n", b"canonical\n", Path(directory) / "merge")

    def test_noop_canonical_result_stops_verification(self):
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(ValueError):
            r2_verify.merge_candidate_source(b"audited\n", b"audited\n", b"audited\n", Path(directory) / "merge")

    def test_clean_audited_source_keeps_exact_canonical_bytes_and_bom(self):
        audited = b"\xef\xbb\xbforiginal\r\n"
        canonical = b"\xef\xbb\xbfcanonical\r\n"
        with tempfile.TemporaryDirectory() as directory:
            result = r2_verify.merge_candidate_source(audited, audited, canonical, Path(directory) / "merge")
        self.assertEqual(result, canonical)

if __name__ == "__main__":
    unittest.main()
