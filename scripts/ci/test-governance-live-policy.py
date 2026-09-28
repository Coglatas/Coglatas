#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("evaluate-governance-live-policy.py")
SPEC = importlib.util.spec_from_file_location("governance_live_policy", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Unable to load Governance live-policy evaluator")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def policy_fixture():
    return {
        "version": 1,
        "policy_id": "COGLATAS-GOVERNANCE",
        "repository": "Coglatas/Coglatas",
        "default_branch": "main",
        "controls": [
            {
                "id": "GOV-RULESET-001",
                "enforcement": "blocking",
                "expected": {
                    "target": "default-branch",
                    "ruleset_names": [
                        "Public Main Protection - Strict External Review",
                        "PRreview",
                    ],
                    "strict_required_status_checks": True,
                    "branch_deletion_allowed": False,
                    "non_fast_forward_allowed": False,
                },
            },
            {
                "id": "GOV-CHECKS-001",
                "enforcement": "blocking",
                "expected": {
                    "strict": True,
                    "required": [
                        {"context": "External PR approval policy"},
                        {"context": "build-test"},
                        {"context": "frontend-test"},
                        {"context": "security-scan"},
                        {"context": "publication-readiness"},
                    ],
                },
            },
            {
                "id": "GOV-SIGNATURE-001",
                "enforcement": "blocking",
                "expected": {"required": True},
            },
            {
                "id": "GOV-REVIEW-001",
                "enforcement": "blocking",
                "expected": {
                    "pull_request_required": True,
                    "approvals_required": 1,
                    "codeowners_review_required": True,
                    "unattributed_changes_require_additional_approval": True,
                    "independent_approval_required": True,
                    "author_cannot_self_approve": True,
                    "minimum_distinct_codeowners": 2,
                    "external_pr_approval_reviewer": "@NYGsatoshi",
                },
            },
            {
                "id": "GOV-BYPASS-001",
                "enforcement": "blocking",
                "expected": {
                    "allowed_actors": [],
                    "normal_development_bypass_forbidden": True,
                },
            },
        ],
    }


def live_fixture():
    required_checks = [
        {"context": "External PR approval policy"},
        {"context": "build-test", "integration_id": 15368},
        {"context": "frontend-test", "integration_id": 15368},
        {"context": "security-scan", "integration_id": 15368},
        {"context": "publication-readiness", "integration_id": 15368},
    ]
    return {
        "repository": {
            "full_name": "Coglatas/Coglatas",
            "default_branch": "main",
        },
        "branch": {"name": "main", "protected": True},
        "rulesets": [
            {
                "id": 22158959,
                "name": "Public Main Protection - Strict External Review",
                "target": "branch",
                "source_type": "Repository",
                "source": "Coglatas/Coglatas",
                "enforcement": "active",
                "conditions": {
                    "ref_name": {
                        "exclude": [],
                        "include": ["~DEFAULT_BRANCH"],
                    }
                },
                "rules": [
                    {"type": "deletion"},
                    {"type": "non_fast_forward"},
                    {
                        "type": "required_status_checks",
                        "parameters": {
                            "strict_required_status_checks_policy": True,
                            "do_not_enforce_on_create": False,
                            "required_status_checks": required_checks,
                        },
                    },
                    {"type": "required_signatures"},
                ],
                "bypass_actors": [],
            },
            {
                "id": 22167589,
                "name": "PRreview",
                "target": "branch",
                "source_type": "Repository",
                "source": "Coglatas/Coglatas",
                "enforcement": "active",
                "conditions": {
                    "ref_name": {
                        "exclude": [],
                        "include": ["~DEFAULT_BRANCH"],
                    }
                },
                "rules": [
                    {"type": "deletion"},
                    {"type": "non_fast_forward"},
                    {
                        "type": "pull_request",
                        "parameters": {
                            "required_approving_review_count": 1,
                            "dismiss_stale_reviews_on_push": False,
                            "required_reviewers": [],
                            "require_code_owner_review": True,
                            "require_last_push_approval": False,
                            "required_review_thread_resolution": False,
                            "require_extra_approval_for_unattributed_changes": True,
                            "allowed_merge_methods": [
                                "merge",
                                "squash",
                                "rebase",
                            ],
                        },
                    },
                ],
                "bypass_actors": [],
            },
            {
                "id": 21115493,
                "name": "BranchProtection",
                "target": "branch",
                "source_type": "Repository",
                "source": "Coglatas/Coglatas",
                "enforcement": "disabled",
                "conditions": {
                    "ref_name": {
                        "exclude": [],
                        "include": ["~DEFAULT_BRANCH"],
                    }
                },
                "rules": [],
                "bypass_actors": [],
            },
        ],
    }


class GovernanceLivePolicyTests(unittest.TestCase):
    def evaluate(self, live=None):
        return MODULE.evaluate_policy(
            policy_fixture(), live or live_fixture(), "a" * 40
        )

    def codes(self, report):
        return {item["code"] for item in report["findings"]}

    def test_exact_expected_state_passes(self):
        report = self.evaluate()
        self.assertEqual("PASS", report["status"])
        self.assertEqual([], report["findings"])
        self.assertEqual(64, len(report["snapshot_sha256"]))

    def test_required_ruleset_disabled_fails(self):
        live = live_fixture()
        live["rulesets"][0]["enforcement"] = "disabled"
        self.assertIn(
            "REQUIRED_RULESET_NOT_ACTIVE", self.codes(self.evaluate(live))
        )

    def test_required_context_removed_fails(self):
        live = live_fixture()
        checks = live["rulesets"][0]["rules"][2]["parameters"][
            "required_status_checks"
        ]
        checks.pop()
        self.assertIn(
            "REQUIRED_CONTEXT_MISSING", self.codes(self.evaluate(live))
        )

    def test_review_count_reduced_fails(self):
        live = live_fixture()
        live["rulesets"][1]["rules"][2]["parameters"][
            "required_approving_review_count"
        ] = 0
        self.assertIn("APPROVAL_COUNT_DRIFT", self.codes(self.evaluate(live)))

    def test_required_signatures_removed_fails(self):
        live = live_fixture()
        live["rulesets"][0]["rules"] = [
            rule
            for rule in live["rulesets"][0]["rules"]
            if rule["type"] != "required_signatures"
        ]
        self.assertIn(
            "REQUIRED_SIGNATURES_MISSING", self.codes(self.evaluate(live))
        )

    def test_unexpected_bypass_actor_fails(self):
        live = live_fixture()
        live["rulesets"][0]["bypass_actors"] = [
            {
                "actor_id": 123,
                "actor_type": "Integration",
                "bypass_mode": "pull_request",
            }
        ]
        self.assertIn(
            "UNEXPECTED_BYPASS_ACTOR", self.codes(self.evaluate(live))
        )

    def test_unobservable_bypass_actor_set_fails_closed(self):
        live = live_fixture()
        del live["rulesets"][0]["bypass_actors"]
        self.assertIn(
            "BYPASS_ACTORS_UNOBSERVABLE", self.codes(self.evaluate(live))
        )

    def test_unexpected_active_default_branch_ruleset_fails(self):
        live = live_fixture()
        extra = copy.deepcopy(live["rulesets"][2])
        extra["id"] = 999
        extra["name"] = "Unexpected active"
        extra["enforcement"] = "active"
        live["rulesets"].append(extra)
        self.assertIn(
            "UNEXPECTED_ACTIVE_RULESET", self.codes(self.evaluate(live))
        )

    def test_unprotected_default_branch_fails(self):
        live = live_fixture()
        live["branch"]["protected"] = False
        self.assertIn(
            "DEFAULT_BRANCH_UNPROTECTED", self.codes(self.evaluate(live))
        )

    def test_malformed_live_response_is_rejected(self):
        with self.assertRaises(MODULE.EvaluationError):
            MODULE.evaluate_policy(
                policy_fixture(), {"repository": {}}, "a" * 40
            )

    def test_unordered_equivalent_json_has_same_snapshot_hash(self):
        left = live_fixture()
        right = copy.deepcopy(left)
        right["rulesets"].reverse()
        public_ruleset = next(
            item
            for item in right["rulesets"]
            if item["name"]
            == "Public Main Protection - Strict External Review"
        )
        public_ruleset["rules"].reverse()
        checks_rule = next(
            rule
            for rule in public_ruleset["rules"]
            if rule["type"] == "required_status_checks"
        )
        checks_rule["parameters"]["required_status_checks"].reverse()
        first = self.evaluate(left)
        second = self.evaluate(right)
        self.assertEqual(
            first["snapshot_sha256"], second["snapshot_sha256"]
        )
        self.assertEqual("PASS", second["status"])


if __name__ == "__main__":
    unittest.main()
