#!/usr/bin/env python3
"""Fail-closed reconciliation of repository Governance policy with live GitHub rulesets.

The trusted workflow fetches authoritative GitHub API responses and the default-branch
policy, writes them as JSON, and invokes this evaluator. This module never treats live
state as a baseline and intentionally has no network dependency so fixture tests can
exercise the exact comparison logic used by CI.
"""

from __future__ import annotations

import argparse
import copy
import fnmatch
import hashlib
import json
import sys
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1
REQUIRED_CONTROL_IDS = (
    "GOV-RULESET-001",
    "GOV-CHECKS-001",
    "GOV-SIGNATURE-001",
    "GOV-REVIEW-001",
    "GOV-BYPASS-001",
)


class EvaluationError(ValueError):
    """Raised when policy/live input cannot be evaluated safely."""


def _canonicalize(value: Any) -> Any:
    if isinstance(value, dict):
        return {key: _canonicalize(value[key]) for key in sorted(value)}
    if isinstance(value, list):
        normalized = [_canonicalize(item) for item in value]
        return sorted(
            normalized,
            key=lambda item: json.dumps(
                item, ensure_ascii=False, sort_keys=True, separators=(",", ":")
            ),
        )
    return value


def _canonical_json(value: Any) -> str:
    return json.dumps(
        _canonicalize(value),
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    )


def _sha256_json(value: Any) -> str:
    return hashlib.sha256(_canonical_json(value).encode("utf-8")).hexdigest()


def _require_object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise EvaluationError(f"{label} must be an object")
    return value


def _require_array(value: Any, label: str) -> list[Any]:
    if not isinstance(value, list):
        raise EvaluationError(f"{label} must be an array")
    return value


def _control(policy: dict[str, Any], control_id: str) -> dict[str, Any]:
    controls = _require_array(policy.get("controls"), "policy.controls")
    matches = [
        item
        for item in controls
        if isinstance(item, dict) and item.get("id") == control_id
    ]
    if len(matches) != 1:
        raise EvaluationError(
            f"policy must contain exactly one {control_id}; found {len(matches)}"
        )
    control = matches[0]
    if control.get("enforcement") != "blocking":
        raise EvaluationError(
            f"{control_id} must remain blocking for GOV-02 evaluation"
        )
    return control


def _default_ref_tokens(default_branch: str) -> set[str]:
    return {"~DEFAULT_BRANCH", default_branch, f"refs/heads/{default_branch}"}


def _condition_matches_default(rule_set: dict[str, Any], default_branch: str) -> bool:
    conditions = rule_set.get("conditions")
    if conditions is None:
        return True
    conditions = _require_object(
        conditions, f"ruleset {rule_set.get('name')!r}.conditions"
    )
    ref_name = conditions.get("ref_name")
    if ref_name is None:
        return True
    ref_name = _require_object(
        ref_name, f"ruleset {rule_set.get('name')!r}.conditions.ref_name"
    )
    includes = _require_array(
        ref_name.get("include", []), "conditions.ref_name.include"
    )
    excludes = _require_array(
        ref_name.get("exclude", []), "conditions.ref_name.exclude"
    )
    if not all(isinstance(item, str) for item in includes + excludes):
        raise EvaluationError(
            "ruleset ref_name include/exclude entries must be strings"
        )

    tokens = _default_ref_tokens(default_branch)
    full_ref = f"refs/heads/{default_branch}"

    def matches(pattern: str) -> bool:
        if pattern in tokens:
            return True
        return fnmatch.fnmatchcase(full_ref, pattern) or fnmatch.fnmatchcase(
            default_branch, pattern
        )

    included = not includes or any(matches(item) for item in includes)
    excluded = any(matches(item) for item in excludes)
    return included and not excluded


def _normalize_rule(rule: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(rule.get("type"), str) or not rule["type"]:
        raise EvaluationError("every ruleset rule must have a non-empty type")
    normalized: dict[str, Any] = {"type": rule["type"]}
    if "parameters" in rule:
        normalized["parameters"] = _canonicalize(
            _require_object(
                rule["parameters"], f"rule {rule['type']}.parameters"
            )
        )
    return normalized


def _normalize_bypass_actor(actor: Any) -> dict[str, Any]:
    actor = _require_object(actor, "bypass actor")
    required = ("actor_id", "actor_type", "bypass_mode")
    if any(key not in actor for key in required):
        raise EvaluationError(
            "bypass actor must contain actor_id, actor_type, and bypass_mode"
        )
    return {
        "actor_id": actor["actor_id"],
        "actor_type": actor["actor_type"],
        "bypass_mode": actor["bypass_mode"],
    }


def normalize_live_state(live_state: dict[str, Any]) -> dict[str, Any]:
    repository = _require_object(live_state.get("repository"), "live.repository")
    branch = _require_object(live_state.get("branch"), "live.branch")
    rulesets = _require_array(live_state.get("rulesets"), "live.rulesets")

    repository_id = repository.get("id")
    full_name = repository.get("full_name")
    default_branch = repository.get("default_branch")
    if not isinstance(repository_id, int) or isinstance(repository_id, bool) or repository_id <= 0:
        raise EvaluationError(
            "live.repository.id must be a positive integer"
        )
    if not isinstance(full_name, str) or not full_name:
        raise EvaluationError(
            "live.repository.full_name must be a non-empty string"
        )
    if not isinstance(default_branch, str) or not default_branch:
        raise EvaluationError(
            "live.repository.default_branch must be a non-empty string"
        )
    if branch.get("name") != default_branch:
        raise EvaluationError(
            "live.branch.name must equal live.repository.default_branch"
        )
    if not isinstance(branch.get("protected"), bool):
        raise EvaluationError("live.branch.protected must be a boolean")

    normalized_rulesets: list[dict[str, Any]] = []
    seen_ids: set[Any] = set()
    for raw in rulesets:
        item = _require_object(raw, "ruleset")
        for key in (
            "id",
            "name",
            "target",
            "source_type",
            "source",
            "enforcement",
            "rules",
        ):
            if key not in item:
                raise EvaluationError(
                    f"ruleset is missing required field {key!r}"
                )
        if item["id"] in seen_ids:
            raise EvaluationError(f"duplicate ruleset id {item['id']!r}")
        seen_ids.add(item["id"])
        if not isinstance(item["name"], str) or not item["name"]:
            raise EvaluationError("ruleset.name must be a non-empty string")

        normalized: dict[str, Any] = {
            "id": item["id"],
            "name": item["name"],
            "target": item["target"],
            "source_type": item["source_type"],
            "source": item["source"],
            "enforcement": item["enforcement"],
            "conditions": _canonicalize(item.get("conditions", {})),
            "rules": sorted(
                [
                    _normalize_rule(rule)
                    for rule in _require_array(item["rules"], "ruleset.rules")
                ],
                key=lambda rule: (rule["type"], _canonical_json(rule)),
            ),
            "applies_to_default_branch": _condition_matches_default(
                item, default_branch
            ),
            "bypass_actors_observable": "bypass_actors" in item,
        }
        if "bypass_actors" in item:
            normalized["bypass_actors"] = sorted(
                [
                    _normalize_bypass_actor(actor)
                    for actor in _require_array(
                        item["bypass_actors"], "ruleset.bypass_actors"
                    )
                ],
                key=lambda actor: (
                    str(actor["actor_type"]),
                    str(actor["actor_id"]),
                    str(actor["bypass_mode"]),
                ),
            )
        normalized_rulesets.append(normalized)

    normalized_rulesets.sort(
        key=lambda item: (str(item["name"]), str(item["id"]))
    )
    return {
        "repository": {
            "id": repository_id,
            "full_name": full_name,
            "default_branch": default_branch,
        },
        "branch": {
            "name": branch["name"],
            "protected": branch["protected"],
        },
        "rulesets": normalized_rulesets,
    }


def _finding(control: str, code: str, message: str) -> dict[str, str]:
    return {"control": control, "code": code, "message": message}


def _effective_rulesets(snapshot: dict[str, Any]) -> list[dict[str, Any]]:
    return [
        item
        for item in snapshot["rulesets"]
        if item["target"] == "branch"
        and item["applies_to_default_branch"]
        and item["enforcement"] == "active"
    ]


def _expected_rulesets(
    snapshot: dict[str, Any], expected_names: list[str]
) -> list[dict[str, Any]]:
    names = set(expected_names)
    return [
        item
        for item in snapshot["rulesets"]
        if item["target"] == "branch"
        and item["applies_to_default_branch"]
        and item["name"] in names
    ]


def _status_contexts(rule_sets: list[dict[str, Any]]) -> set[str]:
    contexts: set[str] = set()
    for rule_set in rule_sets:
        if rule_set["enforcement"] != "active":
            continue
        for rule in rule_set["rules"]:
            if rule["type"] != "required_status_checks":
                continue
            params = _require_object(
                rule.get("parameters"), "required_status_checks.parameters"
            )
            checks = _require_array(
                params.get("required_status_checks"),
                "required_status_checks.parameters.required_status_checks",
            )
            for check in checks:
                check = _require_object(check, "required status check")
                context = check.get("context")
                if not isinstance(context, str) or not context:
                    raise EvaluationError(
                        "required status check context must be a non-empty string"
                    )
                contexts.add(context)
    return contexts


def _review_parameters(
    rule_sets: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    params: list[dict[str, Any]] = []
    for rule_set in rule_sets:
        if rule_set["enforcement"] != "active":
            continue
        for rule in rule_set["rules"]:
            if rule["type"] == "pull_request":
                params.append(
                    _require_object(
                        rule.get("parameters"), "pull_request.parameters"
                    )
                )
    return params


def evaluate_policy(
    policy: dict[str, Any],
    live_state: dict[str, Any],
    target_sha: str | None = None,
) -> dict[str, Any]:
    for control_id in REQUIRED_CONTROL_IDS:
        _control(policy, control_id)

    repository = policy.get("repository")
    repository_id = policy.get("repository_id")
    repository_aliases = policy.get("repository_aliases")
    default_branch = policy.get("default_branch")
    if not isinstance(repository, str) or not repository:
        raise EvaluationError("policy.repository must be a non-empty string")
    if not isinstance(repository_id, int) or isinstance(repository_id, bool) or repository_id <= 0:
        raise EvaluationError("policy.repository_id must be a positive integer")
    if (
        not isinstance(repository_aliases, list)
        or not repository_aliases
        or not all(isinstance(item, str) and item for item in repository_aliases)
        or repository not in repository_aliases
    ):
        raise EvaluationError(
            "policy.repository_aliases must contain the canonical repository name"
        )
    if not isinstance(default_branch, str) or not default_branch:
        raise EvaluationError(
            "policy.default_branch must be a non-empty string"
        )

    snapshot = normalize_live_state(live_state)
    findings: list[dict[str, str]] = []

    if snapshot["repository"]["id"] != repository_id:
        findings.append(
            _finding(
                "GOV-RULESET-001",
                "REPOSITORY_ID_MISMATCH",
                f"expected repository id {repository_id!r}, got "
                f"{snapshot['repository']['id']!r}",
            )
        )
    if snapshot["repository"]["full_name"] not in repository_aliases:
        findings.append(
            _finding(
                "GOV-RULESET-001",
                "REPOSITORY_NAME_NOT_ALLOWED",
                f"repository name {snapshot['repository']['full_name']!r} is not "
                f"one of the transfer-safe aliases {repository_aliases!r}",
            )
        )
    if snapshot["repository"]["default_branch"] != default_branch:
        findings.append(
            _finding(
                "GOV-RULESET-001",
                "DEFAULT_BRANCH_MISMATCH",
                f"expected default branch {default_branch!r}, got "
                f"{snapshot['repository']['default_branch']!r}",
            )
        )
    if not snapshot["branch"]["protected"]:
        findings.append(
            _finding(
                "GOV-RULESET-001",
                "DEFAULT_BRANCH_UNPROTECTED",
                "GitHub reports the default branch as unprotected",
            )
        )

    ruleset_control = _control(policy, "GOV-RULESET-001")
    ruleset_expected = _require_object(
        ruleset_control.get("expected"), "GOV-RULESET-001.expected"
    )
    expected_names_raw = _require_array(
        ruleset_expected.get("ruleset_names"), "ruleset_names"
    )
    if not expected_names_raw or not all(
        isinstance(item, str) and item for item in expected_names_raw
    ):
        raise EvaluationError(
            "GOV-RULESET-001.expected.ruleset_names must contain non-empty strings"
        )
    expected_names = list(expected_names_raw)
    if len(set(expected_names)) != len(expected_names):
        raise EvaluationError(
            "GOV-RULESET-001.expected.ruleset_names contains duplicates"
        )

    relevant = _expected_rulesets(snapshot, expected_names)
    effective = _effective_rulesets(snapshot)
    for name in expected_names:
        matches = [item for item in relevant if item["name"] == name]
        if len(matches) != 1:
            findings.append(
                _finding(
                    "GOV-RULESET-001",
                    "REQUIRED_RULESET_MISSING_OR_AMBIGUOUS",
                    f"expected exactly one default-branch ruleset named "
                    f"{name!r}; found {len(matches)}",
                )
            )
            continue
        if matches[0]["enforcement"] != "active":
            findings.append(
                _finding(
                    "GOV-RULESET-001",
                    "REQUIRED_RULESET_NOT_ACTIVE",
                    f"ruleset {name!r} enforcement is "
                    f"{matches[0]['enforcement']!r}, expected 'active'",
                )
            )

    expected_name_set = set(expected_names)
    unexpected_active = [
        item["name"]
        for item in effective
        if item["name"] not in expected_name_set
    ]
    if unexpected_active:
        findings.append(
            _finding(
                "GOV-RULESET-001",
                "UNEXPECTED_ACTIVE_RULESET",
                "unexpected active default-branch rulesets: "
                + ", ".join(sorted(unexpected_active)),
            )
        )

    active_expected = [
        item for item in relevant if item["enforcement"] == "active"
    ]
    active_rule_types = {
        rule["type"] for item in active_expected for rule in item["rules"]
    }
    if (
        ruleset_expected.get("branch_deletion_allowed") is False
        and "deletion" not in active_rule_types
    ):
        findings.append(
            _finding(
                "GOV-RULESET-001",
                "DELETION_PROTECTION_MISSING",
                "default-branch deletion protection rule is missing",
            )
        )
    if (
        ruleset_expected.get("non_fast_forward_allowed") is False
        and "non_fast_forward" not in active_rule_types
    ):
        findings.append(
            _finding(
                "GOV-RULESET-001",
                "NON_FAST_FORWARD_PROTECTION_MISSING",
                "default-branch non-fast-forward protection rule is missing",
            )
        )

    checks_control = _control(policy, "GOV-CHECKS-001")
    checks_expected = _require_object(
        checks_control.get("expected"), "GOV-CHECKS-001.expected"
    )
    required_entries = _require_array(
        checks_expected.get("required"), "GOV-CHECKS-001.expected.required"
    )
    expected_contexts: set[str] = set()
    for entry in required_entries:
        entry = _require_object(entry, "required check policy entry")
        context = entry.get("context")
        if not isinstance(context, str) or not context:
            raise EvaluationError(
                "required check policy entry has invalid context"
            )
        expected_contexts.add(context)

    actual_contexts = _status_contexts(active_expected)
    for context in sorted(expected_contexts - actual_contexts):
        findings.append(
            _finding(
                "GOV-CHECKS-001",
                "REQUIRED_CONTEXT_MISSING",
                f"required status context {context!r} is missing from live rulesets",
            )
        )
    for context in sorted(actual_contexts - expected_contexts):
        findings.append(
            _finding(
                "GOV-CHECKS-001",
                "UNEXPECTED_REQUIRED_CONTEXT",
                f"live rulesets require unregistered context {context!r}",
            )
        )

    if (
        checks_expected.get("strict") is True
        or ruleset_expected.get("strict_required_status_checks") is True
    ):
        status_rules = [
            rule
            for item in active_expected
            for rule in item["rules"]
            if rule["type"] == "required_status_checks"
        ]
        if not status_rules:
            findings.append(
                _finding(
                    "GOV-CHECKS-001",
                    "REQUIRED_STATUS_RULE_MISSING",
                    "no active required_status_checks rule applies to the default branch",
                )
            )
        for rule in status_rules:
            params = _require_object(
                rule.get("parameters"), "required_status_checks.parameters"
            )
            if params.get("strict_required_status_checks_policy") is not True:
                findings.append(
                    _finding(
                        "GOV-CHECKS-001",
                        "STRICT_REQUIRED_STATUS_DISABLED",
                        "strict_required_status_checks_policy is not true",
                    )
                )

    signature_control = _control(policy, "GOV-SIGNATURE-001")
    signature_expected = _require_object(
        signature_control.get("expected"), "GOV-SIGNATURE-001.expected"
    )
    signatures_present = "required_signatures" in active_rule_types
    if signature_expected.get("required") is True and not signatures_present:
        findings.append(
            _finding(
                "GOV-SIGNATURE-001",
                "REQUIRED_SIGNATURES_MISSING",
                "required_signatures is absent from active default-branch rulesets",
            )
        )
    if signature_expected.get("required") is False and signatures_present:
        findings.append(
            _finding(
                "GOV-SIGNATURE-001",
                "UNEXPECTED_REQUIRED_SIGNATURES",
                "required_signatures is active but policy does not expect it",
            )
        )

    review_control = _control(policy, "GOV-REVIEW-001")
    review_expected = _require_object(
        review_control.get("expected"), "GOV-REVIEW-001.expected"
    )
    review_params = _review_parameters(active_expected)
    if review_expected.get("pull_request_required") is True and not review_params:
        findings.append(
            _finding(
                "GOV-REVIEW-001",
                "PULL_REQUEST_RULE_MISSING",
                "no active pull_request ruleset rule applies to the default branch",
            )
        )
    if review_params:
        approval_counts: list[int] = []
        for params in review_params:
            value = params.get("required_approving_review_count")
            if not isinstance(value, int) or isinstance(value, bool):
                raise EvaluationError(
                    "pull_request.required_approving_review_count must be an integer"
                )
            approval_counts.append(value)
        live_approval_count = max(approval_counts)
        expected_approval_count = review_expected.get("approvals_required")
        if live_approval_count != expected_approval_count:
            findings.append(
                _finding(
                    "GOV-REVIEW-001",
                    "APPROVAL_COUNT_DRIFT",
                    f"required approvals live={live_approval_count!r} "
                    f"expected={expected_approval_count!r}",
                )
            )
        live_codeowner = any(
            params.get("require_code_owner_review") is True
            for params in review_params
        )
        if live_codeowner is not (
            review_expected.get("codeowners_review_required") is True
        ):
            findings.append(
                _finding(
                    "GOV-REVIEW-001",
                    "CODEOWNER_REVIEW_DRIFT",
                    f"require_code_owner_review live={live_codeowner!r} "
                    f"expected={review_expected.get('codeowners_review_required')!r}",
                )
            )
        live_unattributed = any(
            params.get("require_extra_approval_for_unattributed_changes") is True
            for params in review_params
        )
        if live_unattributed is not (
            review_expected.get(
                "unattributed_changes_require_additional_approval"
            )
            is True
        ):
            findings.append(
                _finding(
                    "GOV-REVIEW-001",
                    "UNATTRIBUTED_APPROVAL_DRIFT",
                    "require_extra_approval_for_unattributed_changes does not match policy",
                )
            )

    bypass_control = _control(policy, "GOV-BYPASS-001")
    bypass_expected = _require_object(
        bypass_control.get("expected"), "GOV-BYPASS-001.expected"
    )
    allowed_actors = _require_array(
        bypass_expected.get("allowed_actors"),
        "GOV-BYPASS-001.expected.allowed_actors",
    )
    if allowed_actors:
        raise EvaluationError(
            "GOV-02 currently supports the GOV-01 no-bypass baseline only; "
            "GOV-03 must define the actor schema before non-empty allowlists are accepted"
        )

    observed_bypass: list[dict[str, Any]] = []
    for item in active_expected:
        if not item.get("bypass_actors_observable"):
            findings.append(
                _finding(
                    "GOV-BYPASS-001",
                    "BYPASS_ACTORS_UNOBSERVABLE",
                    f"GitHub omitted bypass_actors for ruleset {item['name']!r}; "
                    "evaluator cannot safely infer an empty set",
                )
            )
            continue
        for actor in item.get("bypass_actors", []):
            observed_bypass.append(
                {"ruleset": item["name"], **copy.deepcopy(actor)}
            )
    if observed_bypass and not allowed_actors:
        for actor in observed_bypass:
            findings.append(
                _finding(
                    "GOV-BYPASS-001",
                    "UNEXPECTED_BYPASS_ACTOR",
                    "unexpected bypass actor "
                    f"ruleset={actor['ruleset']!r} "
                    f"type={actor['actor_type']!r} "
                    f"id={actor['actor_id']!r} "
                    f"mode={actor['bypass_mode']!r}",
                )
            )

    controls: list[dict[str, Any]] = []
    for control_id in REQUIRED_CONTROL_IDS:
        control_findings = [
            item for item in findings if item["control"] == control_id
        ]
        controls.append(
            {
                "id": control_id,
                "status": "FAIL" if control_findings else "PASS",
                "finding_codes": sorted(
                    {item["code"] for item in control_findings}
                ),
            }
        )

    return {
        "schema_version": SCHEMA_VERSION,
        "policy_id": policy.get("policy_id"),
        "policy_version": policy.get("version"),
        "repository": repository,
        "repository_id": repository_id,
        "default_branch": default_branch,
        "target_sha": target_sha,
        "status_context": "GOV-RULESET-001",
        "status": "FAIL" if findings else "PASS",
        "snapshot_sha256": _sha256_json(snapshot),
        "controls": controls,
        "findings": findings,
        "snapshot": snapshot,
    }


def _write_report(path: Path | None, report: dict[str, Any]) -> None:
    text = json.dumps(
        report, ensure_ascii=False, sort_keys=True, indent=2
    ) + "\n"
    if path is None:
        sys.stdout.write(text)
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")


def _invalid_report(
    exc: Exception, target_sha: str | None
) -> dict[str, Any]:
    return {
        "schema_version": SCHEMA_VERSION,
        "policy_id": None,
        "policy_version": None,
        "repository": None,
        "default_branch": None,
        "target_sha": target_sha,
        "status_context": "GOV-RULESET-001",
        "status": "FAIL",
        "snapshot_sha256": None,
        "controls": [],
        "findings": [
            {
                "control": "GOV-RULESET-001",
                "code": "INPUT_OR_EVALUATOR_FAILURE",
                "message": str(exc),
            }
        ],
        "snapshot": None,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--policy", required=True, type=Path)
    parser.add_argument("--live-state", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--target-sha")
    args = parser.parse_args(argv)

    try:
        policy = _require_object(
            json.loads(args.policy.read_text(encoding="utf-8")), "policy"
        )
        live_state = _require_object(
            json.loads(args.live_state.read_text(encoding="utf-8")),
            "live state",
        )
        report = evaluate_policy(policy, live_state, args.target_sha)
    except (
        OSError,
        json.JSONDecodeError,
        EvaluationError,
        KeyError,
        TypeError,
        ValueError,
    ) as exc:
        report = _invalid_report(exc, args.target_sha)
        _write_report(args.output, report)
        return 2

    _write_report(args.output, report)
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
