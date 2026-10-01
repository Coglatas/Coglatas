"""Reject drift in R2 baselines, fixed tests, and allowed source overlaps."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import r2_verify

class R2BaselineTests(unittest.TestCase):
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
