#!/usr/bin/env python3
from __future__ import annotations

import copy
import datetime as dt
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from typing import Any

MODULE_PATH = Path(__file__).with_name("check-required-pr-checks.py")
SPEC = importlib.util.spec_from_file_location("required_check_guard", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Unable to load required PR check guard")
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)

REGISTRY = guard.load_required_check_registry()
HEAD = "a" * 40
OLD_HEAD = "b" * 40
NOW = dt.datetime(2026, 9, 4, 5, 30, tzinfo=dt.timezone.utc)


def dual_registry(context: str = "build-test-v2") -> dict[str, Any]:
    result = copy.deepcopy(REGISTRY)
    build = next(item for item in result["checks"] if item["gate_id"] == "GOV-GATE-BUILD-001")
    build["rename"] = {
        "state": "dual-publish",
        "previous_context": "build-test",
        "previous_workflow": ".github/workflows/ci.yml",
        "previous_job": "build-test-old",
        "migration_issue": 900,
    }
    build["context"] = context
    build["job"] = "build-test-v2"
    return result


class RegistryTests(unittest.TestCase):
    def test_registry_matches_governance_policy(self) -> None:
        self.assertEqual(4, len(REGISTRY["checks"]))
        self.assertEqual(
            ["build-test", "frontend-test", "security-scan", "publication-readiness"],
            [item["context"] for item in REGISTRY["checks"]],
        )

    def test_dual_publish_expands_previous_identity(self) -> None:
        registry = dual_registry()
        expanded = [item for item in guard.expanded_checks(registry) if item["source_gate_id"] == "GOV-GATE-BUILD-001"]
        self.assertEqual(2, len(expanded))
        self.assertEqual({"build-test", "build-test-v2"}, {item["context"] for item in expanded})

    def test_stable_rename_cannot_keep_previous_metadata(self) -> None:
        bad = copy.deepcopy(REGISTRY)
        bad["checks"][0]["rename"]["previous_context"] = "old"
        with tempfile.TemporaryDirectory() as td:
            p = Path(td)
            registry_path = p / "registry.json"
            policy_path = p / "policy.json"
            registry_path.write_text(json.dumps(bad), encoding="utf-8")
            policy_path.write_text(Path(guard.POLICY_PATH).read_text(encoding="utf-8"), encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "stable state"):
                guard.load_required_check_registry(registry_path, policy_path)


class StaticTopologyTests(unittest.TestCase):
    def test_unfiltered_required_job_passes(self) -> None:
        text = """
name: Publication Readiness
on:
  pull_request:
jobs:
  publication-readiness:
    name: publication-readiness
    runs-on: ubuntu-latest
    timeout-minutes: 20
"""
        self.assertEqual([], guard.required_check_errors(".github/workflows/publication-readiness.yml", text, REGISTRY))

    def test_paths_filter_is_rejected(self) -> None:
        text = """
on:
  pull_request:
    paths: ["src/**"]
jobs:
  publication-readiness:
    name: publication-readiness
    runs-on: ubuntu-latest
    timeout-minutes: 20
"""
        errors = guard.required_check_errors(".github/workflows/publication-readiness.yml", text, REGISTRY)
        self.assertTrue(any("unfiltered pull_request" in error for error in errors))

    def test_job_level_if_is_rejected(self) -> None:
        text = """
on:
  pull_request:
jobs:
  publication-readiness:
    name: publication-readiness
    if: github.actor != 'x'
    runs-on: ubuntu-latest
    timeout-minutes: 20
"""
        errors = guard.required_check_errors(".github/workflows/publication-readiness.yml", text, REGISTRY)
        self.assertTrue(any("job-level if" in error for error in errors))

    def test_needs_is_rejected(self) -> None:
        text = """
on:
  pull_request:
jobs:
  publication-readiness:
    name: publication-readiness
    needs: prepare
    runs-on: ubuntu-latest
    timeout-minutes: 20
"""
        errors = guard.required_check_errors(".github/workflows/publication-readiness.yml", text, REGISTRY)
        self.assertTrue(any("must not depend" in error for error in errors))

    def test_declared_build_prerequisite_passes(self) -> None:
        text = """
on:
  pull_request:
jobs:
  dotnet-build:
    name: .NET build producer
    runs-on: ubuntu-latest
  build-test:
    name: build-test
    needs: dotnet-build
    runs-on: ubuntu-latest
    timeout-minutes: 120
"""
        errors = guard.required_check_errors(".github/workflows/ci.yml", text, REGISTRY)
        self.assertEqual([], errors)

    def test_declared_prerequisite_with_job_if_is_rejected(self) -> None:
        text = """
on:
  pull_request:
jobs:
  dotnet-build:
    name: .NET build producer
    if: github.actor != 'x'
    runs-on: ubuntu-latest
  build-test:
    name: build-test
    needs: dotnet-build
    runs-on: ubuntu-latest
    timeout-minutes: 120
"""
        errors = guard.required_check_errors(".github/workflows/ci.yml", text, REGISTRY)
        self.assertTrue(any("prerequisite job 'dotnet-build' must not use job-level if" in error for error in errors))

    def test_continue_on_error_is_rejected(self) -> None:
        text = """
on:
  pull_request:
jobs:
  publication-readiness:
    name: publication-readiness
    continue-on-error: true
    runs-on: ubuntu-latest
    timeout-minutes: 20
"""
        errors = guard.required_check_errors(".github/workflows/publication-readiness.yml", text, REGISTRY)
        self.assertTrue(any("continue-on-error" in error for error in errors))

    def test_dual_publish_requires_both_jobs_statically(self) -> None:
        registry = dual_registry()
        text = """
on:
  pull_request:
jobs:
  build-test-v2:
    name: build-test-v2
    runs-on: ubuntu-latest
    timeout-minutes: 120
"""
        errors = guard.required_check_errors(".github/workflows/ci.yml", text, registry)
        self.assertTrue(any("build-test-old" in error for error in errors))


class LiveRulesetTests(unittest.TestCase):
    def live_ruleset(self, registry: dict[str, Any] = REGISTRY) -> dict[str, Any]:
        checks = []
        for context, integration in guard.live_expected(registry):
            value: dict[str, Any] = {"context": context}
            if integration is not None:
                value["integration_id"] = integration
            checks.append(value)
        return {
            "name": registry["ruleset"]["name"],
            "target": "branch",
            "enforcement": "active",
            "conditions": {"ref_name": {"exclude": [], "include": ["~DEFAULT_BRANCH"]}},
            "rules": [{"type": "required_status_checks", "parameters": {"strict_required_status_checks_policy": True, "required_status_checks": checks}}],
        }

    def test_exact_live_ruleset_passes(self) -> None:
        self.assertEqual([], guard.live_ruleset_errors(REGISTRY, self.live_ruleset()))

    def test_missing_required_context_fails(self) -> None:
        live = self.live_ruleset()
        live["rules"][0]["parameters"]["required_status_checks"].pop()
        self.assertTrue(any("missing required context" in e for e in guard.live_ruleset_errors(REGISTRY, live)))

    def test_wrong_integration_fails(self) -> None:
        live = self.live_ruleset()
        build = next(item for item in live["rules"][0]["parameters"]["required_status_checks"] if item["context"] == "build-test")
        build["integration_id"] = 999
        self.assertTrue(any("producer integration drift" in e for e in guard.live_ruleset_errors(REGISTRY, live)))

    def test_dual_publish_old_and_new_are_both_expected(self) -> None:
        registry = dual_registry()
        live = self.live_ruleset(registry)
        contexts = {item["context"] for item in live["rules"][0]["parameters"]["required_status_checks"]}
        self.assertIn("build-test", contexts)
        self.assertIn("build-test-v2", contexts)
        self.assertEqual([], guard.live_ruleset_errors(registry, live))

    def test_dual_publish_missing_old_context_fails(self) -> None:
        registry = dual_registry()
        live = self.live_ruleset(registry)
        values = live["rules"][0]["parameters"]["required_status_checks"]
        values[:] = [item for item in values if item["context"] != "build-test"]
        errors = guard.live_ruleset_errors(registry, live)
        self.assertTrue(any("missing required context 'build-test'" in e for e in errors))


class ExactHeadTests(unittest.TestCase):
    def success_evidence(self, registry: dict[str, Any] = REGISTRY, head: str = HEAD) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
        checks: list[dict[str, Any]] = []
        statuses: list[dict[str, Any]] = []
        for offset, item in enumerate(guard.exact_entries(registry)):
            timestamp = f"2026-09-04T05:{10 + offset:02d}:00Z"
            if item["kind"] == "workflow-job":
                checks.append({
                    "id": offset + 1,
                    "name": item["context"],
                    "head_sha": head,
                    "status": "completed",
                    "conclusion": "success",
                    "completed_at": timestamp,
                    "app": {"id": item["producer"]["integration_id"], "slug": item["producer"]["app_slug"]},
                    "workflow": item["workflow"],
                    "workflow_event": "pull_request",
                    "workflow_head_sha": head,
                })
            else:
                statuses.append({
                    "id": offset + 1,
                    "context": item["context"],
                    "sha": head,
                    "state": "success",
                    "updated_at": timestamp,
                    "creator": {"login": item["producer"]["creator_login"]},
                    "workflow": item["workflow"],
                    "workflow_event": "workflow_run",
                    "workflow_head_branch": "main",
                })
        return checks, statuses

    def test_current_head_success_passes(self) -> None:
        checks, statuses = self.success_evidence()
        report = guard.exact_head_report(REGISTRY, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
        self.assertEqual("pass", report["decision"])

    def test_previous_head_only_fails(self) -> None:
        checks, statuses = self.success_evidence()
        build = next(item for item in checks if item["name"] == "build-test")
        build["head_sha"] = OLD_HEAD
        build["workflow_head_sha"] = OLD_HEAD
        report = guard.exact_head_report(REGISTRY, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
        gate = next(item for item in report["gates"] if item["context"] == "build-test")
        self.assertEqual("previous-head-only", gate["reason"])
        self.assertEqual("fail", report["decision"])

    def test_skipped_cancelled_neutral_fail(self) -> None:
        for conclusion in ("skipped", "cancelled", "neutral"):
            with self.subTest(conclusion=conclusion):
                checks, statuses = self.success_evidence()
                next(item for item in checks if item["name"] == "build-test")["conclusion"] = conclusion
                report = guard.exact_head_report(REGISTRY, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
                self.assertEqual("fail", report["decision"])

    def test_pending_before_timeout_is_pending(self) -> None:
        checks, statuses = self.success_evidence()
        build = next(item for item in checks if item["name"] == "build-test")
        build.update(status="in_progress", conclusion=None, started_at="2026-09-04T05:00:00Z")
        report = guard.exact_head_report(REGISTRY, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
        self.assertEqual("pending", report["decision"])

    def test_pending_after_timeout_fails(self) -> None:
        checks, statuses = self.success_evidence()
        publication = next(item for item in checks if item["name"] == "publication-readiness")
        publication.update(status="in_progress", conclusion=None, started_at="2026-09-04T04:00:00Z")
        report = guard.exact_head_report(REGISTRY, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
        self.assertEqual("fail", report["decision"])
        gate = next(item for item in report["gates"] if item["context"] == "publication-readiness")
        self.assertEqual("pending-timeout", gate["reason"])

    def test_wrong_producer_fails(self) -> None:
        checks, statuses = self.success_evidence()
        next(item for item in checks if item["name"] == "build-test")["app"] = {"id": 1, "slug": "other"}
        report = guard.exact_head_report(REGISTRY, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
        self.assertEqual("fail", report["decision"])

    def test_dual_publish_requires_old_and_new_exact_head(self) -> None:
        registry = dual_registry()
        checks, statuses = self.success_evidence(registry)
        report = guard.exact_head_report(registry, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
        build_gates = [g for g in report["gates"] if g["source_gate_id"] == "GOV-GATE-BUILD-001"]
        self.assertEqual(2, len(build_gates))
        self.assertEqual("pass", report["decision"])
        checks[:] = [item for item in checks if item["name"] != "build-test"]
        report = guard.exact_head_report(registry, HEAD, checks, statuses, now=NOW, trusted_base_ref="main")
        self.assertEqual("fail", report["decision"])
        old = next(g for g in report["gates"] if g["context"] == "build-test")
        self.assertEqual("missing-current-head", old["reason"])


class FakeApi:
    def __init__(self, responses: dict[str, Any]) -> None:
        self.responses = responses
        self.calls: list[str] = []

    def get(self, path: str) -> Any:
        self.calls.append(path)
        if path not in self.responses:
            raise RuntimeError(f"unexpected API call: {path}")
        return copy.deepcopy(self.responses[path])


class LiveEvaluatorTests(ExactHeadTests, LiveRulesetTests):
    def test_authoritative_head_and_base_are_refetched(self) -> None:
        checks, statuses = self.success_evidence(head=HEAD)
        live = self.live_ruleset()
        responses: dict[str, Any] = {
            "repos/Coglatas/Coglatas/pulls/42": {"state": "open", "head": {"sha": HEAD}, "base": {"ref": "main"}},
            "repos/Coglatas/Coglatas/rulesets": [{"id": 123, "name": REGISTRY["ruleset"]["name"]}],
            "repos/Coglatas/Coglatas/rulesets/123": live,
            f"repos/Coglatas/Coglatas/commits/{HEAD}/check-runs?filter=latest&per_page=100": {"total_count": len(checks), "check_runs": checks},
            f"repos/Coglatas/Coglatas/commits/{HEAD}/statuses?per_page=100": statuses,
        }
        for candidate in checks:
            run_id = 1000 + candidate["id"]
            candidate["details_url"] = f"https://github.com/Coglatas/Coglatas/actions/runs/{run_id}/job/{candidate['id']}"
            responses[f"repos/Coglatas/Coglatas/actions/runs/{run_id}"] = {
                "path": candidate["workflow"], "event": "pull_request", "head_sha": HEAD, "head_branch": "feature"
            }
        for candidate in statuses:
            run_id = 2000 + candidate["id"]
            candidate["target_url"] = f"https://github.com/Coglatas/Coglatas/actions/runs/{run_id}"
            responses[f"repos/Coglatas/Coglatas/actions/runs/{run_id}"] = {
                "path": candidate["workflow"], "event": candidate["workflow_event"], "head_sha": "c" * 40, "head_branch": "main"
            }
        responses[f"repos/Coglatas/Coglatas/commits/{HEAD}/check-runs?filter=latest&per_page=100"] = {"total_count": len(checks), "check_runs": checks}
        responses[f"repos/Coglatas/Coglatas/commits/{HEAD}/statuses?per_page=100"] = statuses
        report = guard.evaluate_live_pr(FakeApi(responses), "Coglatas/Coglatas", 42, REGISTRY, now=NOW)
        self.assertEqual(HEAD, report["authoritative_head_sha"])
        self.assertEqual("main", report["authoritative_base_ref"])
        self.assertEqual("pass", report["decision"])

    def test_api_failure_is_fail_closed_by_caller(self) -> None:
        repository = "Coglatas/Coglatas"
        api = FakeApi({})
        with self.assertRaises(RuntimeError):
            guard.evaluate_live_pr(api, repository, 42, REGISTRY, now=NOW)


if __name__ == "__main__":
    unittest.main()
