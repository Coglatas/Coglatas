"""Exercise missing/stale/flaky evidence and artifact boundary failures."""
import copy
import io
import json
import os
import tempfile
import textwrap
import unittest
import zipfile
import importlib.util
import urllib.request
import hashlib
from unittest.mock import patch
from datetime import date
from pathlib import Path

from functional_evidence import OWNERS, expected_owners, validate_manifest

SHA = "a" * 40


def complete_manifest():
    lanes = [{
        "schemaVersion": 1, "commitSha": SHA, "gate": "functional-full", "runId": "100", "runAttempt": "1",
        "suite": suite, "startedAt": "2026-10-04T00:00:00Z", "completedAt": "2026-10-04T00:00:10Z",
        "setupSeconds": 10, "testSeconds": 10,
        "journeys": [{"journeyId": owner, "status": "PASS", "attempts": 1, "durationMs": 1000}
                     for owner in expected_owners(suite, "functional-full")],
    } for suite in OWNERS]
    return {"schemaVersion": 1, "commitSha": SHA, "gate": "functional-full", "runId": "100", "runAttempt": "1", "lanes": lanes}


class FunctionalEvidenceTests(unittest.TestCase):
    def validate(self, data):
        return validate_manifest(data, SHA, "functional-full", "100", "1")

    def execute_extended_producer(self, runs):
        workflow = Path(__file__).resolve().parents[2] / ".github/workflows/functional-extended.yml"
        source = workflow.read_text(encoding="utf-8").split("          python3 - <<'PY'\n", 1)[1].split("          PY", 1)[0]
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "producer-output"
            environment = {"GITHUB_API_URL": "https://api.github.com", "GITHUB_REPOSITORY": "NYGsatoshi/Coglatas",
                           "TARGET_SHA": SHA, "GITHUB_TOKEN": "synthetic-token", "GITHUB_OUTPUT": str(output)}
            response = io.BytesIO(json.dumps({"workflow_runs": runs}).encode())
            with patch.dict(os.environ, environment), patch("urllib.request.urlopen", return_value=response):
                exec(compile(textwrap.dedent(source), str(workflow), "exec"), {})
            return output.read_text(encoding="utf-8")

    def main_producer(self, **changes):
        return {"id": 100, "head_sha": SHA, "event": "push", "head_branch": "main",
                "path": ".github/workflows/main-build-artifacts.yml", "status": "completed",
                "conclusion": "success", **changes}

    def test_extended_selects_latest_completed_trusted_producer(self):
        runs = [self.main_producer(id=101), self.main_producer()]
        self.assertEqual(self.execute_extended_producer(runs), "run_id=101\n")

    def test_extended_latest_cancelled_cannot_fall_back_to_older_success(self):
        runs = [self.main_producer(), self.main_producer(id=101, conclusion="cancelled")]
        with self.assertRaisesRegex(AssertionError, "older runs cannot substitute"):
            self.execute_extended_producer(runs)

    def test_extended_latest_incomplete_cannot_fall_back_to_older_success(self):
        for state in ("queued", "in_progress", "completed"):
            with self.subTest(state=state):
                runs = [self.main_producer(), self.main_producer(id=101, status=state, conclusion=None)]
                with self.assertRaisesRegex(AssertionError, "older runs cannot substitute"):
                    self.execute_extended_producer(runs)

    def test_extended_rejects_wrong_producer_trust_identity(self):
        for field, value in (("head_sha", "b" * 40), ("event", "pull_request"),
                             ("head_branch", "candidate"), ("path", ".github/workflows/ci.yml")):
            with self.subTest(field=field):
                untrusted = self.main_producer(id=101, **{field: value})
                with self.assertRaisesRegex(AssertionError, "No trusted exact-candidate"):
                    self.execute_extended_producer([untrusted])
                self.assertEqual(self.execute_extended_producer([self.main_producer(), untrusted]), "run_id=100\n")

    def test_complete_exact_run_passes(self):
        self.validate(complete_manifest())

    def test_missing_duplicate_and_unknown_domains_fail(self):
        for mutation in (lambda value: value["lanes"].pop(),
                         lambda value: value["lanes"].__setitem__(1, copy.deepcopy(value["lanes"][0]))):
            data = complete_manifest()
            mutation(data)
            with self.assertRaises(ValueError):
                self.validate(data)

    def test_stale_commit_run_attempt_and_schema_fail(self):
        for field, value in (("commitSha", "b" * 40), ("runId", "99"), ("runAttempt", "2"), ("schemaVersion", 2)):
            data = complete_manifest()
            data["lanes"][0][field] = value
            with self.assertRaises(ValueError):
                self.validate(data)

    def test_no_failure_state_or_retry_can_be_green(self):
        for status in ("FAIL", "FLAKY", "SKIPPED", "QUARANTINED", "BLOCKED", "NOT_RUN"):
            data = complete_manifest()
            data["lanes"][0]["journeys"][0]["status"] = status
            with self.assertRaises(ValueError):
                self.validate(data)
        data = complete_manifest()
        data["lanes"][0]["journeys"][0]["attempts"] = 2
        with self.assertRaises(ValueError):
            self.validate(data)

    def test_missing_duplicate_and_protected_owner_fields_fail(self):
        for field, value in (("journeyId", "FUNC-FILE-002"), ("protectedBody", "do not retain")):
            data = complete_manifest()
            data["lanes"][0]["journeys"][0][field] = value
            with self.assertRaises(ValueError):
                self.validate(data)
        data = complete_manifest()
        data["lanes"][0]["journeys"] = []
        with self.assertRaises(ValueError):
            self.validate(data)

    def test_zip_reader_rejects_paths_extra_files_and_oversize(self):
        spec = importlib.util.spec_from_file_location("final_checks", Path(__file__).with_name("verify-mvp-a-final-checks.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        for paths in (("../manifest.json",), ("manifest.json", "protected.txt")):
            buffer = io.BytesIO()
            with zipfile.ZipFile(buffer, "w") as archive:
                for path in paths:
                    archive.writestr(path, json.dumps(complete_manifest()))
            with self.assertRaises(RuntimeError):
                module.read_manifest_archive(buffer.getvalue())
        with self.assertRaises(RuntimeError):
            module.read_manifest_archive(b"x" * (1024 * 1024 + 1))

    def test_signed_redirect_drops_api_authorization(self):
        spec = importlib.util.spec_from_file_location("final_checks", Path(__file__).with_name("verify-mvp-a-final-checks.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        request = urllib.request.Request("https://api.github.com/artifact", headers={"Authorization": "Bearer synthetic-token"})
        redirected = module.MetadataRedirectHandler().redirect_request(request, None, 302, "Found", {}, "https://storage.example.test/metadata.zip")
        self.assertFalse(redirected.has_header("Authorization"))

    def test_quarantine_requires_tracking_metadata_and_expiring_review(self):
        spec = importlib.util.spec_from_file_location("quarantine", Path(__file__).with_name("validate-functional-quarantine.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        entry = {"issue": "https://github.com/NYGsatoshi/Coglatas/issues/611", "reason": "Tracked synthetic instability",
                 "owner": "ci-maintainers", "domain": "core", "quarantinedAt": "2026-10-01", "reviewBy": "2026-10-10",
                 "journeyId": "FUNC-TASK-001", "p0ReadinessImpact": "Required owner remains blocked"}
        self.assertEqual(module.validate({"schemaVersion": 1, "entries": [entry]}, date(2026, 10, 4)), 1)
        for altered in (dict(entry, reviewBy="2026-10-03"), {key: value for key, value in entry.items() if key != "issue"}):
            with self.assertRaises(ValueError):
                module.validate({"schemaVersion": 1, "entries": [altered]}, date(2026, 10, 4))

    def test_live_consumer_binds_latest_trusted_main_run_attempt_and_digest(self):
        spec = importlib.util.spec_from_file_location("final_checks", Path(__file__).with_name("verify-mvp-a-final-checks.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        check = {"id": 1000, "name": "functional-full", "head_sha": SHA, "status": "completed", "conclusion": "success",
                 "app": {"id": module.GITHUB_ACTIONS_APP_ID, "slug": module.GITHUB_ACTIONS_APP_SLUG},
                 "details_url": "https://github.com/NYGsatoshi/Coglatas/actions/runs/100/job/200"}
        run = {"head_sha": SHA, "event": "push", "head_branch": "main", "path": ".github/workflows/main-build-artifacts.yml",
               "status": "completed", "conclusion": "success", "run_attempt": 1}
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("manifest.json", json.dumps(complete_manifest()))
        archive = buffer.getvalue()
        artifact = {"id": 300, "name": f"functional-evidence-functional-full-{SHA}-1", "expired": False,
                    "digest": "sha256:" + hashlib.sha256(archive).hexdigest()}
        class Opener:
            def open(self, *args, **kwargs):
                return io.BytesIO(archive)
        with patch.object(module, "fetch_page", side_effect=[run, {"artifacts": [artifact], "total_count": 1}]), patch.object(module.urllib.request, "build_opener", return_value=Opener()):
            module.verify_functional_evidence([check], "NYGsatoshi/Coglatas", SHA, "synthetic-token", "https://api.github.com")
        for altered in (dict(run, head_sha="b" * 40), dict(run, event="pull_request"), dict(run, conclusion="cancelled"), dict(run, status="in_progress")):
            with patch.object(module, "fetch_page", return_value=altered), self.assertRaises(RuntimeError):
                module.verify_functional_evidence([check], "NYGsatoshi/Coglatas", SHA, "synthetic-token", "https://api.github.com")
        newer = dict(check, id=1001, status="queued", conclusion=None)
        with self.assertRaises(RuntimeError):
            module.verify_functional_evidence([check, newer], "NYGsatoshi/Coglatas", SHA, "synthetic-token", "https://api.github.com")


if __name__ == "__main__":
    unittest.main()
