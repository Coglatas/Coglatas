#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXT_PATH = Path(__file__).with_name("evaluate-governance-live-policy-ext.py")
LEGACY_TEST_PATH = Path(__file__).with_name("test-governance-live-policy.py")

EXT_SPEC = importlib.util.spec_from_file_location("governance_live_policy_ext", EXT_PATH)
if EXT_SPEC is None or EXT_SPEC.loader is None:
    raise RuntimeError("Unable to load extended GOV-02 evaluator")
EXT = importlib.util.module_from_spec(EXT_SPEC)
EXT_SPEC.loader.exec_module(EXT)

LEGACY_SPEC = importlib.util.spec_from_file_location("governance_live_policy_legacy_tests", LEGACY_TEST_PATH)
if LEGACY_SPEC is None or LEGACY_SPEC.loader is None:
    raise RuntimeError("Unable to load legacy GOV-02 fixture module")
LEGACY = importlib.util.module_from_spec(LEGACY_SPEC)
LEGACY_SPEC.loader.exec_module(LEGACY)


def _control(policy, control_id):
    return next(item for item in policy["controls"] if item["id"] == control_id)


def policy_fixture():
    policy = copy.deepcopy(LEGACY.policy_fixture())
    _control(policy, "GOV-REVIEW-001")["expected"] = {
        "pull_request_required": True,
        "approvals_required": 0,
        "codeowners_review_required": False,
        "unattributed_changes_require_additional_approval": False,
        "dismiss_stale_reviews_on_push": False,
        "require_last_push_approval": False,
        "required_review_thread_resolution": True,
        "author_cannot_self_approve": True,
        "minimum_distinct_codeowners": 1,
        "external_pr_current_head_approval_required": True,
        "external_pr_approval_reviewer": "@NYGsatoshi",
        "external_approval_reviewer_must_be_codeowner": True,
        "owner_authored_pr_external_approval_required": False,
        "draft_pr_blocks": True,
        "changes_requested_blocks": True,
        "previous_head_approval_satisfies": False,
        "codeowners_fallback_required": True,
        "codeowners_required_explicit_patterns": ["/.github/"],
        "codeowners_sensitive_paths": [".github/CODEOWNERS"],
    }
    _control(policy, "GOV-BYPASS-001")["expected"] = {
        "allowed_actors": [],
        "normal_development_bypass_forbidden": True,
        "user_always_bypass": "forbidden",
        "break_glass_usage_evidence_required": True,
    }
    return policy


def live_fixture():
    live = copy.deepcopy(LEGACY.live_fixture())
    params = live["rulesets"][1]["rules"][2]["parameters"]
    params["required_approving_review_count"] = 0
    params["require_code_owner_review"] = False
    params["require_extra_approval_for_unattributed_changes"] = False
    params["dismiss_stale_reviews_on_push"] = False
    params["require_last_push_approval"] = False
    params["required_review_thread_resolution"] = True
    live["branch"]["protection"] = {
        "enabled": False,
        "required_status_checks": {"contexts": [], "checks": []},
    }
    live["classic_branch_protection"] = {
        "observable": False,
        "configured": None,
        "http_status": 403,
        "error_class": "forbidden",
        "details": None,
    }
    return live


class GovernanceLivePolicyGov03IntegrationTests(unittest.TestCase):
    def evaluate(self, policy=None, live=None):
        return EXT.evaluate_policy(
            policy or policy_fixture(), live or live_fixture(), "b" * 40
        )

    @staticmethod
    def codes(report):
        return {item["code"] for item in report["findings"]}

    def test_gov03_target_review_contract_passes(self):
        report = self.evaluate()
        self.assertEqual("PASS", report["status"])
        self.assertEqual([], report["findings"])
        self.assertEqual(64, len(report["snapshot_sha256"]))
        self.assertEqual(
            "gov03-review-bypass-classic-v1", report["evaluator_extension"]
        )

    def test_review_thread_resolution_drift_fails(self):
        live = live_fixture()
        live["rulesets"][1]["rules"][2]["parameters"][
            "required_review_thread_resolution"
        ] = False
        self.assertIn(
            "REVIEW_THREAD_RESOLUTION_DRIFT", self.codes(self.evaluate(live=live))
        )

    def test_dismiss_stale_review_switch_drift_fails(self):
        live = live_fixture()
        live["rulesets"][1]["rules"][2]["parameters"][
            "dismiss_stale_reviews_on_push"
        ] = True
        self.assertIn(
            "DISMISS_STALE_REVIEWS_DRIFT", self.codes(self.evaluate(live=live))
        )

    def test_last_push_switch_drift_fails(self):
        live = live_fixture()
        live["rulesets"][1]["rules"][2]["parameters"][
            "require_last_push_approval"
        ] = True
        self.assertIn(
            "LAST_PUSH_APPROVAL_DRIFT", self.codes(self.evaluate(live=live))
        )

    def test_gov03_allowlisted_integration_can_match_exactly(self):
        policy = policy_fixture()
        policy["controls"][-1]["expected"]["allowed_actors"] = [
            {
                "actor_type": "Integration",
                "actor_id": 29110,
                "bypass_mode": "pull_request",
                "purpose": "trusted merge integration",
                "rationale": "explicitly reviewed integration exception",
                "break_glass": False,
                "normal_development": False,
                "rulesets": ["Public Main Protection - Strict External Review"],
            }
        ]
        live = live_fixture()
        live["rulesets"][0]["bypass_actors"] = [
            {
                "actor_type": "Integration",
                "actor_id": 29110,
                "bypass_mode": "pull_request",
            }
        ]
        self.assertEqual("PASS", self.evaluate(policy=policy, live=live)["status"])

    def test_unexpected_bypass_actor_fails(self):
        live = live_fixture()
        live["rulesets"][0]["bypass_actors"] = [
            {"actor_type": "Integration", "actor_id": 29110, "bypass_mode": "pull_request"}
        ]
        self.assertIn("UNEXPECTED_BYPASS_ACTOR", self.codes(self.evaluate(live=live)))

    def test_expected_bypass_actor_missing_fails(self):
        policy = policy_fixture()
        _control(policy, "GOV-BYPASS-001")["expected"]["allowed_actors"] = [
            {
                "actor_type": "Integration",
                "actor_id": 29110,
                "bypass_mode": "pull_request",
                "purpose": "trusted merge integration",
                "rationale": "explicitly reviewed integration exception",
                "break_glass": False,
                "normal_development": False,
                "rulesets": ["Public Main Protection - Strict External Review"],
            }
        ]
        self.assertIn(
            "EXPECTED_BYPASS_ACTOR_MISSING", self.codes(self.evaluate(policy=policy))
        )

    def test_classic_branch_protection_details_are_hashed_when_observable(self):
        live = live_fixture()
        live["classic_branch_protection"] = {
            "observable": True,
            "configured": True,
            "http_status": 200,
            "error_class": None,
            "details": {
                "required_status_checks": {"strict": True, "contexts": ["build-test"]},
                "enforce_admins": True,
            },
        }
        report = self.evaluate(live=live)
        self.assertEqual("PASS", report["status"])
        self.assertTrue(report["snapshot"]["classic_branch_protection"]["configured"])
        self.assertIn(
            "CLASSIC_BRANCH_PROTECTION_PRESENT",
            {item["code"] for item in report["observations"]},
        )

    def test_classic_protection_403_is_explicit_observation_not_fake_absence(self):
        report = self.evaluate()
        classic = report["snapshot"]["classic_branch_protection"]
        self.assertFalse(classic["observable"])
        self.assertIsNone(classic["configured"])
        self.assertIn(
            "CLASSIC_BRANCH_PROTECTION_UNOBSERVABLE",
            {item["code"] for item in report["observations"]},
        )

    def test_retired_approval_evaluator_is_non_blocking_compatibility_only(self):
        evaluator = (ROOT / ".github/workflows/external-pr-approval-evaluator.yml").read_text(encoding="utf-8")
        audit = (ROOT / ".github/workflows/governance-live-policy-audit.yml").read_text(encoding="utf-8")
        fetcher = (ROOT / "scripts/ci/fetch-governance-live-state.sh").read_text(encoding="utf-8")
        self.assertIn("Governance approval gate retired", evaluator)
        self.assertIn("publishes no commit status", evaluator)
        self.assertNotIn("statuses: write", evaluator)
        self.assertNotIn("human-merge-approval", evaluator)
        self.assertIn("branch_protection_rule", audit)
        self.assertIn('cron: "17 * * * *"', audit)
        self.assertIn("/protection", fetcher)
        self.assertIn("classic_branch_protection", fetcher)


if __name__ == "__main__":
    unittest.main()
