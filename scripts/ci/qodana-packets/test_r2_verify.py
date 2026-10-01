"""Reject drift in R2 baselines, fixed tests, and allowed source overlaps."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import r2_verify

class R2BaselineTests(unittest.TestCase):
    def test_p14_requires_unchanged_production_and_has_no_supplied_fixed_test(self):
        r2_verify.require_test(Path("unused"), "P14")
        r2_verify.require_baseline_paths(["scripts/ci/qodana-packets/selections/P14.json"], "P14", True)
        for path in ["src/Coglatas.Web/Realtime/HubSubscriptionRegistry.cs", r2_verify.FIXED_TESTS["P11"][0]]:
            with self.subTest(path=path):
                with self.assertRaises(ValueError):
                    r2_verify.require_baseline_paths([path], "P14", True)
        with self.assertRaises(ValueError):
            r2_verify.require_test(Path("unused"), "P15")

    def test_main_integration_allows_only_own_fixed_test_and_auxiliary_files(self):
        own = r2_verify.FIXED_TESTS["P11"][0]
        r2_verify.require_baseline_paths([own, "scripts/ci/qodana-packets/selections/P11.json"], "P11", True)
        for path in ["src/changed.cs", "frontend/package-lock.json", r2_verify.FIXED_TESTS["P13"][0]]:
            with self.subTest(path=path):
                with self.assertRaises(ValueError):
                    r2_verify.require_baseline_paths([path], "P11", True)
        with self.assertRaises(ValueError):
            r2_verify.require_baseline_paths([own], "P11", False)

    def payload(self):
        root = Path(__file__).resolve().parent
        return json.loads((root / "r2-payload.json").read_text()), json.loads((root / "payload.json").read_text())

    def test_accepts_pinned_payload_and_rejects_helper_plan_or_fixed_test_drift(self):
        payload, original = self.payload()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "payload.json").write_text(json.dumps(original))
            (root / "r2-payload.json").write_text(json.dumps(payload))
            r2_verify.load_payload(root)
            for kind in ["helper", "plan", "test", "path"]:
                changed = copy.deepcopy(payload)
                if kind == "helper":
                    changed["helper"] += "\n"
                elif kind == "plan":
                    changed["plan"]["audit_source_sha"] = "0" * 40
                elif kind == "test":
                    changed["tests"]["P11"]["content"] += "\n"
                else:
                    changed["tests"]["P11"]["path"] = "tests/other.cs"
                (root / "r2-payload.json").write_text(json.dumps(changed))
                with self.subTest(kind=kind):
                    with self.assertRaises(ValueError):
                        r2_verify.load_payload(root)

    def test_fixed_test_requires_exact_bytes(self):
        payload, _ = self.payload()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            test = payload["tests"]["P13"]
            target = root / test["path"]
            target.parent.mkdir(parents=True)
            target.write_bytes(test["content"].encode())
            r2_verify.require_test(root, "P13")
            target.write_bytes(test["content"].encode() + b"\n")
            with self.assertRaises(ValueError):
                r2_verify.require_test(root, "P13")

    def test_source_overlap_accepts_only_audited_or_exact_known_upstream_form(self):
        root = Path(__file__).resolve().parent
        _, original, namespace = r2_verify.load_payload(root)
        path = "src/Coglatas.Infrastructure/Persistence/AuditPackageExportService.cs"
        audited = b"claimIds.Contains(finding.ArtifactClaimId)"
        current = b"Enumerable.Contains(claimIds, finding.ArtifactClaimId)"
        self.assertIsNone(r2_verify.upstream_variant("P11", path, audited, audited, original, namespace))
        self.assertEqual(r2_verify.upstream_variant("P11", path, audited, current, original, namespace), "P04")
        for changed in [b"Enumerable.Contains<Guid>(claimIds, finding.ArtifactClaimId)", b"unapproved"]:
            with self.subTest(changed=changed):
                with self.assertRaises(ValueError):
                    r2_verify.upstream_variant("P11", path, audited, changed, original, namespace)
        with self.assertRaises(ValueError):
            r2_verify.upstream_variant("P11", "src/other.cs", audited, current, original, namespace)

if __name__ == "__main__":
    unittest.main()
