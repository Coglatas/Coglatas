from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
COMMON_PATH = ROOT / "scripts" / "performance" / "common.py"
spec = importlib.util.spec_from_file_location("performance_common_test", COMMON_PATH)
assert spec is not None and spec.loader is not None
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)


class PerformanceEnvironmentContractTests(unittest.TestCase):
    def test_db_opt_in_has_a_distinct_fixture_hash_and_preserves_the_base_version(self) -> None:
        with patch.dict("os.environ", {"COGLATAS_PERFORMANCE_DB_CAPTURE_ENABLED": "false"}):
            base_hash = common.fixture_hash("small")
            self.assertEqual(common.FIXTURE_VERSION, common.active_fixture_version())
        with patch.dict("os.environ", {"COGLATAS_PERFORMANCE_DB_CAPTURE_ENABLED": "true"}):
            db_hash = common.fixture_hash("small")
            self.assertEqual(common.DB_FIXTURE_VERSION, common.active_fixture_version())
        self.assertNotEqual(base_hash, db_hash)
        self.assertEqual(db_hash, common.fixture_hash("small", fixture_version=common.DB_FIXTURE_VERSION))

    def test_load_json_accepts_utf8_bom_but_remains_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temp = Path(directory)
            bom_json = temp / "fixture.json"
            bom_json.write_bytes(b"\xef\xbb\xbf" + json.dumps({"schemaVersion": 1}).encode("utf-8"))
            self.assertEqual({"schemaVersion": 1}, common.load_json(bom_json))

            malformed = temp / "malformed.json"
            malformed.write_bytes(b"\xef\xbb\xbf{not-json}")
            with self.assertRaises(common.PerformanceContractError):
                common.load_json(malformed)

            invalid_utf8 = temp / "invalid-utf8.json"
            invalid_utf8.write_bytes(b"{\xff}")
            with self.assertRaises(common.PerformanceContractError):
                common.load_json(invalid_utf8)

    def test_fixture_hash_is_stable_and_profile_specific(self) -> None:
        hashes = []
        for profile in ("small", "medium", "large"):
            first = common.fixture_hash(profile)
            second = common.fixture_hash(profile)
            self.assertEqual(first, second)
            self.assertRegex(first, r"^[0-9a-f]{64}$")
            hashes.append(first)
        self.assertEqual(3, len(set(hashes)))

    def test_fixture_evidence_requires_exact_manifest_cardinalities(self) -> None:
        _, profile = common.load_profile("small")
        evidence = {
            "schemaVersion": 1,
            "fixtureVersion": common.FIXTURE_VERSION,
            "seedManifestVersion": 1,
            "profile": "small",
            "seed": profile["seed"],
            "fixtureHash": common.fixture_hash("small"),
            "migrationStatus": "current",
            "complete": True,
            "cardinalities": profile["counts"],
            "focus": profile["focus"],
            "identities": {
                "tenantSlug": "perf-small",
                "operatorEmail": "perf-small-operator@example.test",
                "workspaceId": "00000000-0000-0000-0000-000000000001",
                "taskListProjectId": "00000000-0000-0000-0000-000000000002",
                "ganttProjectId": "00000000-0000-0000-0000-000000000002",
                "kanbanProjectId": "00000000-0000-0000-0000-000000000002",
            },
        }
        common.validate_fixture_evidence(evidence, "small")
        drifted = json.loads(json.dumps(evidence))
        drifted["cardinalities"]["tasks"] += 1
        with self.assertRaises(common.PerformanceContractError):
            common.validate_fixture_evidence(drifted, "small")

    def test_public_and_production_like_targets_are_rejected(self) -> None:
        for allowed in (
            "http://127.0.0.1:18080",
            "http://localhost:18080",
            "http://coglatas-performance:8080",
            "http://performance-app:8080",
        ):
            self.assertEqual(allowed, common.validate_target(allowed))

        for rejected in (
            "https://127.0.0.1:18080",
            "http://example.com:8080",
            "http://school.example.jp:8080",
            "http://127.0.0.1",
            "http://127.0.0.1:18080/api",
            "http://user:secret@127.0.0.1:18080",
        ):
            with self.subTest(rejected=rejected):
                with self.assertRaises(common.PerformanceContractError):
                    common.validate_target(rejected)

    def test_measurement_envelope_fails_closed(self) -> None:
        verifier = ROOT / "scripts" / "performance" / "verify-samples.py"
        environment = {
            "measurement": {
                "minimumSamples": 5,
                "warmupSamplesExcluded": True,
            }
        }
        with tempfile.TemporaryDirectory() as directory:
            temp = Path(directory)
            environment_path = temp / "environment.json"
            results_path = temp / "results.json"
            environment_path.write_text(json.dumps(environment), encoding="utf-8")

            success = {
                "warmupSamplesExcluded": True,
                "measuredSamples": 5,
                "environmentStable": True,
                "benchmarkExitCode": 0,
                "timedOut": False,
            }
            results_path.write_text(json.dumps(success), encoding="utf-8")
            completed = subprocess.run(
                [
                    sys.executable,
                    str(verifier),
                    "--results",
                    str(results_path),
                    "--environment-contract",
                    str(environment_path),
                ],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, completed.returncode, completed.stderr)

            for mutation in (
                {"measuredSamples": 4},
                {"warmupSamplesExcluded": False},
                {"environmentStable": False},
                {"benchmarkExitCode": 1},
                {"timedOut": True},
            ):
                failing = success | mutation
                results_path.write_text(json.dumps(failing), encoding="utf-8")
                completed = subprocess.run(
                    [
                        sys.executable,
                        str(verifier),
                        "--results",
                        str(results_path),
                        "--environment-contract",
                        str(environment_path),
                    ],
                    check=False,
                    capture_output=True,
                    text=True,
                )
                self.assertNotEqual(0, completed.returncode, mutation)

    def test_environment_contract_keeps_warmup_out_of_measurement(self) -> None:
        environment = json.loads(
            (ROOT / "performance" / "environment.json").read_text(encoding="utf-8")
        )
        self.assertFalse(environment["warmup"]["measured"])
        self.assertTrue(environment["measurement"]["warmupSamplesExcluded"])
        self.assertGreater(environment["warmup"]["iterations"], 0)
        self.assertGreater(environment["measurement"]["minimumSamples"], 0)
        self.assertEqual({"cold", "warm"}, set(environment["warmup"]["browserAssetCachePolicy"]))

    def test_lifecycle_contract_requires_clean_teardown(self) -> None:
        harness = (ROOT / "scripts" / "performance" / "with-environment.sh").read_text(encoding="utf-8")
        self.assertIn("down --volumes --remove-orphans", harness)
        self.assertIn("trap 'status=$?;", harness)
        self.assertIn('timeout "$COMMAND_TIMEOUT"', harness)
        self.assertIn("preflight.py", harness)
        self.assertIn("warmup.py", harness)
        self.assertIn("collect-environment.py", harness)


class PerformanceEnvironmentRepeatStartTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = {
            "fixtureVersion": common.FIXTURE_VERSION,
            "seedManifestVersion": 1,
            "profile": "small",
            "seed": 592001,
            "fixtureHash": "a" * 64,
            "cardinalities": {"tasks": 120},
            "focus": {"projectTasks": 60},
            "identities": {"workspaceId": "00000000-0000-0000-0000-000000000001"},
        }
        self.encoded_fixture = json.dumps(self.fixture).encode("utf-8-sig")

    def compare_fixtures(self, first: bytes | None, second: bytes | None) -> subprocess.CompletedProcess[str]:
        # Exercise the workflow's actual comparison, not a copy of its logic.
        workflow = (ROOT / ".github/workflows/performance-environment.yml").read_text(encoding="utf-8")
        marker = "      - name: Compare deterministic fixture identity and cardinality\n"
        self.assertEqual(1, workflow.count(marker))
        step = workflow.split(marker, 1)[1].split("\n      - name:", 1)[0]
        script = textwrap.dedent(step.split("          python3 - <<'PY'\n", 1)[1].split("          PY", 1)[0])
        first_literal = "'/tmp/perf02-fixture-first.json'"
        self.assertEqual(1, script.count(first_literal))

        with tempfile.TemporaryDirectory() as directory:
            temp = Path(directory)
            first_path = temp / "first.json"
            second_path = temp / "artifacts/performance/small/fixture.json"
            second_path.parent.mkdir(parents=True)
            if first is not None:
                first_path.write_bytes(first)
            if second is not None:
                second_path.write_bytes(second)
            # Isolate only the fixed first-input path; keep both decoding calls intact.
            script = script.replace(first_literal, repr(str(first_path)))
            return subprocess.run(
                [sys.executable, "-c", script],
                cwd=temp,
                check=False,
                capture_output=True,
                text=True,
                timeout=10,
            )

    def test_repeat_start_accepts_utf8_with_or_without_bom(self) -> None:
        for first_encoding in ("utf-8", "utf-8-sig"):
            for second_encoding in ("utf-8", "utf-8-sig"):
                with self.subTest(first=first_encoding, second=second_encoding):
                    completed = self.compare_fixtures(
                        json.dumps(self.fixture).encode(first_encoding),
                        json.dumps(self.fixture).encode(second_encoding),
                    )
                    self.assertEqual(0, completed.returncode, completed.stderr)
                    self.assertIn("repeat-start deterministic fixture verified:", completed.stdout)
                    self.assertIn(self.fixture["fixtureHash"], completed.stdout)

    def test_repeat_start_rejects_drift_in_every_compared_field(self) -> None:
        mutations = {
            "fixtureVersion": common.FIXTURE_VERSION + 1,
            "seedManifestVersion": 2,
            "profile": "medium",
            "seed": 592002,
            "fixtureHash": "b" * 64,
            "cardinalities": {"tasks": 121},
            "focus": {"projectTasks": 61},
            "identities": {"workspaceId": "00000000-0000-0000-0000-000000000002"},
        }
        for key, changed in mutations.items():
            with self.subTest(key=key):
                second = json.dumps(self.fixture | {key: changed}).encode("utf-8-sig")
                completed = self.compare_fixtures(self.encoded_fixture, second)
                self.assertNotEqual(0, completed.returncode)
                self.assertIn(f"PERF-02 repeat-start drift in {key}", completed.stderr)

    def test_repeat_start_rejects_invalid_json_and_encoding_in_either_input(self) -> None:
        for invalid, error in (
            (b"\xef\xbb\xbf{not-json}", "JSONDecodeError"),
            (b"{\xff}", "UnicodeDecodeError"),
            (b"\xef\xbb\xbf" + self.encoded_fixture, "JSONDecodeError"),
        ):
            for side in (0, 1):
                with self.subTest(invalid=invalid, side=side):
                    inputs = [self.encoded_fixture, self.encoded_fixture]
                    inputs[side] = invalid
                    completed = self.compare_fixtures(*inputs)
                    self.assertNotEqual(0, completed.returncode)
                    self.assertIn(error, completed.stderr)

    def test_repeat_start_rejects_missing_compared_fields_in_either_input(self) -> None:
        for key in self.fixture:
            incomplete = {name: value for name, value in self.fixture.items() if name != key}
            for side in (0, 1):
                with self.subTest(key=key, side=side):
                    inputs = [self.encoded_fixture, self.encoded_fixture]
                    inputs[side] = json.dumps(incomplete).encode("utf-8-sig")
                    completed = self.compare_fixtures(*inputs)
                    self.assertNotEqual(0, completed.returncode)
                    self.assertIn("KeyError", completed.stderr)
                    self.assertIn(key, completed.stderr)

    def test_repeat_start_rejects_missing_files(self) -> None:
        for side in (0, 1):
            with self.subTest(side=side):
                inputs: list[bytes | None] = [self.encoded_fixture, self.encoded_fixture]
                inputs[side] = None
                completed = self.compare_fixtures(*inputs)
                self.assertNotEqual(0, completed.returncode)
                self.assertIn("FileNotFoundError", completed.stderr)


if __name__ == "__main__":
    unittest.main()
