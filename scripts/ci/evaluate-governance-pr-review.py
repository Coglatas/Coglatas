#!/usr/bin/env python3
"""Evaluate authoritative PR review state and trusted GOV-06 exact-head state.

`evaluate()` and the CLI are network-free and deterministic. Review state is
reported independently; merge blocking is owned by the ordinary required CI
checks registered in governance/required-checks.json.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

REVIEW_CONTROL_ID = "GOV-REVIEW-001"


def _control(policy: dict[str, Any]) -> dict[str, Any]:
    matches = [
        c for c in policy.get("controls", [])
        if isinstance(c, dict) and c.get("id") == REVIEW_CONTROL_ID
    ]
    if len(matches) != 1:
        raise ValueError(f"policy must define exactly one {REVIEW_CONTROL_ID}")
    return matches[0]


def _timestamp(review: dict[str, Any]) -> str:
    value = review.get("submitted_at")
    return value if isinstance(value, str) else ""


def evaluate(policy: dict[str, Any], state: dict[str, Any]) -> dict[str, str]:
    expected = _control(policy).get("expected")
    if not isinstance(expected, dict):
        raise ValueError(f"{REVIEW_CONTROL_ID}.expected must be an object")

    if state.get("state") != "open":
        return {"state": "failure", "reason": "PR is not open."}
    if expected.get("draft_pr_blocks") is True and state.get("draft") is True:
        return {"state": "failure", "reason": "Draft PR cannot satisfy approval policy."}

    head_sha = state.get("head_sha")
    author = state.get("author")
    if not isinstance(head_sha, str) or not head_sha:
        raise ValueError("head_sha is required")
    if not isinstance(author, str) or not author:
        raise ValueError("author is required")

    raw_reviews = state.get("reviews")
    if not isinstance(raw_reviews, list) or not all(isinstance(item, dict) for item in raw_reviews):
        raise ValueError("reviews must be an array of objects")
    reviews: list[dict[str, Any]] = raw_reviews

    reviewer = expected.get("external_pr_approval_reviewer")
    if not isinstance(reviewer, str) or not reviewer.startswith("@"):
        raise ValueError("external_pr_approval_reviewer is invalid")
    reviewer_login = reviewer[1:]

    if author == reviewer_login and expected.get("owner_authored_pr_external_approval_required") is False:
        return {"state": "success", "reason": "Owner-authored PR; external-author approval is not required."}

    if expected.get("external_pr_current_head_approval_required") is not True:
        raise ValueError("external PR current-head approval must be required")

    current_head_reviews = [
        review for review in reviews
        if isinstance(review.get("user"), dict)
        and review["user"].get("login") == reviewer_login
        and review.get("commit_id") == head_sha
        and review.get("state") in {"APPROVED", "CHANGES_REQUESTED", "DISMISSED"}
    ]
    latest = max(current_head_reviews, key=_timestamp, default=None)
    latest_state = latest.get("state") if isinstance(latest, dict) else "NONE"
    if latest_state != "APPROVED":
        return {
            "state": "failure",
            "reason": f"Current head needs approval by {reviewer_login} (state: {latest_state}).",
        }
    return {"state": "success", "reason": f"Current head approved by {reviewer_login}."}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--policy", required=True, type=Path)
    parser.add_argument("--state", required=True, type=Path)
    args = parser.parse_args()
    policy = json.loads(args.policy.read_text(encoding="utf-8"))
    state = json.loads(args.state.read_text(encoding="utf-8"))
    if not isinstance(policy, dict) or not isinstance(state, dict):
        raise SystemExit("policy and state must be JSON objects")

    decision = evaluate(policy, state)
    print(json.dumps(decision, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
