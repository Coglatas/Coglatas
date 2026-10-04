#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path
from typing import Any

MODULE_PATH = Path(__file__).with_name("verify-mvp-a-final-checks.py")
SPEC = importlib.util.spec_from_file_location("mvp_a_final_checks", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Unable to load MVP-A final check verifier")
verifier = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verifier)

HEAD = "a" * 40
OLD_HEAD = "b" * 40


def check_run(
    name: str,
    run_id: int,
    *,
    status: str = "completed",
    conclusion: str | None = "success",
    head_sha: str = HEAD,
    started_at: str | None = "2026-09-06T05:00:00Z",
    completed_at: str | None = "2026-09-06T05:10:00Z",
) -> dict[str, Any]:
    """Create a test fixture representing a GitHub Actions check run.

    Args:
        name: The check run name
        run_id: The check run ID
        status: The check run status (default: "completed")
        conclusion: The check run conclusion (default: "success")
        head_sha: The commit SHA the check ran against (default: HEAD)
        started_at: ISO 8601 timestamp when the check started
        completed_at: ISO 8601 timestamp when the check completed

    Returns:
        A dict representing a GitHub check run object with the specified properties
    """
    return {
        "id": run_id,
        "name": name,
        "head_sha": head_sha,
        "status": status,
        "conclusion": conclusion,
        "started_at": started_at,
        "completed_at": completed_at,
        "app": {
            "id": verifier.GITHUB_ACTIONS_APP_ID,
            "slug": verifier.GITHUB_ACTIONS_APP_SLUG,
        },
    }


def successful_evidence() -> list[dict[str, Any]]:
    """Generate a complete set of successful check runs for all required checks.

    Returns:
        A list of check run dicts, one for each required check, all successful
    """
    return [
        check_run(name, 1000 + offset)
        for offset, name in enumerate(verifier.REQUIRED_CHECKS)
    ]


class LatestRequiredCheckTests(unittest.TestCase):
    def evaluate(self, checks: list[dict[str, Any]]) -> tuple[list[str], dict[str, bool]]:
        """Helper to evaluate required checks and convert summary to a dict.

        Args:
            checks: List of check run dicts to evaluate

        Returns:
            Tuple of (failures list, summary dict mapping check name to pass/fail)
        """
        failures, summary = verifier.evaluate_required_checks(checks, HEAD)
        return failures, dict(summary)

    def test_all_latest_exact_head_checks_success(self) -> None:
        failures, summary = self.evaluate(successful_evidence())
        self.assertEqual([], failures)
        self.assertTrue(all(summary.values()))

    def test_observed_main_contexts_report_real_failure_and_missing_functional(self) -> None:
        # Relevant check names, IDs and outcomes from exact main cdf7, not PR aliases.
        main_sha = "cdf7ee0709da722b83e3cb7f52cf56a0d9abc806"
        observed = [
            ("Main Test / Frontend / Security / Main Test", 111319854417, "success"),
            ("Main Test / Frontend / Security / Main Frontend", 111319854442, "success"),
            ("Main Test / Frontend / Security / Main Security", 111319854466, "success"),
            ("Real-backend E2E from main artifacts / licensed-real-backend", 111319854581, "failure"),
            ("SBOM image scan from main artifacts / sbom-image-trusted", 111319854591, "success"),
            ("publication-readiness", 111319302824, "success"),
            ("frontend-static-analysis", 111319302658, "success"),
            ("sbom-source", 111319302866, "success"),
        ]
        checks = [check_run(name, identity, conclusion=outcome, head_sha=main_sha)
                  for name, identity, outcome in observed]
        failures, raw_summary = verifier.evaluate_required_checks(checks, main_sha)
        summary = dict(raw_summary)
        self.assertEqual(9, len(summary))
        self.assertEqual({"functional-full", "Real-backend E2E from main artifacts / licensed-real-backend"},
                         {name for name, green in summary.items() if not green})
        self.assertEqual(2, len(failures))
        self.assertTrue(any("licensed-real-backend: latest trusted check" in failure for failure in failures))
        self.assertTrue(any("functional-full: no trusted" in failure for failure in failures))

    def test_bare_legacy_pr_checks_cannot_satisfy_main_obligations(self) -> None:
        legacy = {
            "Main Test / Frontend / Security / Main Test": "build-test",
            "Main Test / Frontend / Security / Main Frontend": "frontend-test",
            "Main Test / Frontend / Security / Main Security": "security-scan",
            "Real-backend E2E from main artifacts / licensed-real-backend": "licensed-real-backend",
            "SBOM image scan from main artifacts / sbom-image-trusted": "sbom-image-trusted",
        }
        checks = successful_evidence()
        for check in checks:
            check["name"] = legacy.get(check["name"], check["name"])
        failures, summary = self.evaluate(checks)
        self.assertEqual(set(legacy), {name for name, green in summary.items() if not green})
        self.assertEqual(5, len(failures))

    def test_latest_nested_failure_or_pending_cannot_use_old_or_bare_green(self) -> None:
        main_name = "Main Test / Frontend / Security / Main Frontend"
        for status, conclusion in (("completed", "failure"), ("queued", None), ("in_progress", None)):
            with self.subTest(status=status, conclusion=conclusion):
                checks = successful_evidence()
                checks.extend([check_run(main_name, 5000, status=status, conclusion=conclusion),
                               check_run("frontend-test", 6000)])
                failures, summary = self.evaluate(checks)
                self.assertFalse(summary[main_name])
                self.assertEqual(1, len(failures))
                self.assertIn(main_name + ": latest trusted check", failures[0])

    def test_newer_queued_check_without_timestamps_cannot_be_masked_by_old_success(self) -> None:
        checks = successful_evidence()
        old = next(check for check in checks if check["name"] == "Main Test / Frontend / Security / Main Test")
        old["id"] = 2000
        old["completed_at"] = "2026-09-06T06:00:00Z"
        checks.append(
            check_run(
                "Main Test / Frontend / Security / Main Test",
                2001,
                status="queued",
                conclusion=None,
                started_at=None,
                completed_at=None,
            )
        )

        failures, summary = self.evaluate(checks)

        self.assertFalse(summary["Main Test / Frontend / Security / Main Test"])
        self.assertTrue(any("Main Test / Frontend / Security / Main Test: latest trusted check" in failure for failure in failures))

    def test_newer_in_progress_check_cannot_be_masked_by_old_success_finishing_later(self) -> None:
        checks = successful_evidence()
        old = next(check for check in checks if check["name"] == "frontend-static-analysis")
        old["id"] = 2500
        old["completed_at"] = "2026-09-06T06:20:00Z"
        checks.append(
            check_run(
                "frontend-static-analysis",
                2501,
                status="in_progress",
                conclusion=None,
                started_at="2026-09-06T06:10:00Z",
                completed_at=None,
            )
        )

        failures, summary = self.evaluate(checks)

        self.assertFalse(summary["frontend-static-analysis"])
        self.assertTrue(
            any(
                "frontend-static-analysis: latest trusted check" in failure
                for failure in failures
            )
        )

    def test_newer_failure_cannot_be_masked_by_old_success_with_later_timestamp(self) -> None:
        checks = successful_evidence()
        old = next(check for check in checks if check["name"] == "Main Test / Frontend / Security / Main Security")
        old["id"] = 3000
        old["completed_at"] = "2026-09-06T07:00:00Z"
        checks.append(
            check_run(
                "Main Test / Frontend / Security / Main Security",
                3001,
                conclusion="failure",
                started_at="2026-09-06T05:30:00Z",
                completed_at="2026-09-06T05:40:00Z",
            )
        )

        failures, summary = self.evaluate(checks)

        self.assertFalse(summary["Main Test / Frontend / Security / Main Security"])
        self.assertTrue(any("Main Test / Frontend / Security / Main Security: latest trusted check" in failure for failure in failures))

    def test_missing_exact_head_check_fails_even_when_old_head_succeeded(self) -> None:
        checks = successful_evidence()
        publication = next(check for check in checks if check["name"] == "publication-readiness")
        publication["head_sha"] = OLD_HEAD

        failures, summary = self.evaluate(checks)

        self.assertFalse(summary["publication-readiness"])
        self.assertTrue(any("publication-readiness: no trusted" in failure for failure in failures))

    def test_newer_success_replaces_old_failure(self) -> None:
        checks = successful_evidence()
        current = next(check for check in checks if check["name"] == "Main Test / Frontend / Security / Main Frontend")
        current["id"] = 4001
        checks.append(
            check_run(
                "Main Test / Frontend / Security / Main Frontend",
                4000,
                conclusion="failure",
                completed_at="2026-09-06T08:00:00Z",
            )
        )

        failures, summary = self.evaluate(checks)

        self.assertTrue(summary["Main Test / Frontend / Security / Main Frontend"])
        self.assertFalse(any(failure.startswith("Main Test / Frontend / Security / Main Frontend:") for failure in failures))

    def test_invalid_check_run_id_fails_closed(self) -> None:
        checks = successful_evidence()
        build = next(check for check in checks if check["name"] == "Main Test / Frontend / Security / Main Test")
        build.pop("id")

        failures, summary = self.evaluate(checks)

        self.assertFalse(summary["Main Test / Frontend / Security / Main Test"])
        self.assertTrue(
            any(
                "Main Test / Frontend / Security / Main Test: trusted check run has an invalid or missing id" == failure
                for failure in failures
            )
        )


if __name__ == "__main__":
    unittest.main()
