#!/usr/bin/env python3
from __future__ import annotations

import copy
import unittest
from typing import Any

import required_check_contract as guard
import required_check_parent as parent

REGISTRY = guard.load_required_check_registry()
HEAD = "a" * 40
PARENT = "GOV-GATE-EXT-APPROVAL-001"


class FakeApi:
    def __init__(self, responses: dict[str, Any]) -> None:
        self.responses = responses

    def get(self, path: str) -> Any:
        if path not in self.responses:
            raise RuntimeError(f"unexpected API call: {path}")
        return copy.deepcopy(self.responses[path])


def live_ruleset() -> dict[str, Any]:
    checks = []
    for context, integration in guard.live_expected(REGISTRY):
        item: dict[str, Any] = {"context": context}
        if integration is not None:
            item["integration_id"] = integration
        checks.append(item)
    return {
        "name": REGISTRY["ruleset"]["name"],
        "target": "branch",
        "enforcement": "active",
        "conditions": {"ref_name": {"exclude": [], "include": ["~DEFAULT_BRANCH"]}},
        "rules": [{"type": "required_status_checks", "parameters": {
            "strict_required_status_checks_policy": True,
            "required_status_checks": checks,
        }}],
    }


def success_api(*, missing_context: str | None = None, missing_parent_live: bool = False) -> FakeApi:
    checks: list[dict[str, Any]] = []
    responses: dict[str, Any] = {
        "repos/Coglatas/Coglatas/pulls/42": {
            "state": "open", "head": {"sha": HEAD}, "base": {"ref": "main"}
        },
        "repos/Coglatas/Coglatas/rulesets": [
            {"id": 123, "name": REGISTRY["ruleset"]["name"]}
        ],
    }
    live = live_ruleset()
    if missing_parent_live:
        values = live["rules"][0]["parameters"]["required_status_checks"]
        values[:] = [v for v in values if v["context"] != "External PR approval policy"]
    responses["repos/Coglatas/Coglatas/rulesets/123"] = live
    for i, item in enumerate(guard.exact_entries(REGISTRY), start=1):
        if item["gate_id"] == PARENT or item["context"] == missing_context:
            continue
        if item["kind"] != "workflow-job":
            continue
        run_id = 5000 + i
        checks.append({
            "id": i,
            "name": item["context"],
            "head_sha": HEAD,
            "status": "completed",
            "conclusion": "success",
            "completed_at": "2026-09-04T05:20:00Z",
            "app": {"id": item["producer"]["integration_id"], "slug": item["producer"]["app_slug"]},
            "details_url": f"https://github.com/Coglatas/Coglatas/actions/runs/{run_id}/job/{i}",
        })
        responses[f"repos/Coglatas/Coglatas/actions/runs/{run_id}"] = {
            "path": item["workflow"], "event": "pull_request", "head_sha": HEAD, "head_branch": "feature"
        }
    responses[f"repos/Coglatas/Coglatas/commits/{HEAD}/check-runs?filter=latest&per_page=100"] = {
        "total_count": len(checks), "check_runs": checks
    }
    responses[f"repos/Coglatas/Coglatas/commits/{HEAD}/statuses?per_page=100"] = []
    return FakeApi(responses)


class TrustedParentTests(unittest.TestCase):
    def test_parent_status_does_not_create_self_dependency(self) -> None:
        report = parent.evaluate_from_trusted_parent(
            success_api(), "Coglatas/Coglatas", 42, REGISTRY, PARENT
        )
        self.assertEqual("pass", report["decision"])
        gate = next(g for g in report["exact_head"]["gates"] if g["gate_id"] == PARENT)
        self.assertEqual("trusted-parent-satisfied", gate["reason"])

    def test_other_required_check_still_blocks(self) -> None:
        report = parent.evaluate_from_trusted_parent(
            success_api(missing_context="build-test"),
            "Coglatas/Coglatas", 42, REGISTRY, PARENT,
        )
        self.assertEqual("fail", report["decision"])

    def test_live_ruleset_still_requires_parent_context(self) -> None:
        report = parent.evaluate_from_trusted_parent(
            success_api(missing_parent_live=True),
            "Coglatas/Coglatas", 42, REGISTRY, PARENT,
        )
        self.assertEqual("fail", report["decision"])
        self.assertTrue(any("External PR approval policy" in e for e in report["live_ruleset"]["errors"]))

    def test_parent_must_be_commit_status(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "commit-status"):
            parent.evaluate_from_trusted_parent(
                success_api(), "Coglatas/Coglatas", 42, REGISTRY, "GOV-GATE-BUILD-001"
            )


if __name__ == "__main__":
    unittest.main()
