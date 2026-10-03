#!/usr/bin/env python3
"""PERF-03 tool-independent performance result comparator.

The comparator consumes PERF-01 scenario/budget contracts, a PERF-02 environment
fingerprint, current samples, and an approved-main baseline artifact. It emits a
versioned result document and never rounds missing/noisy evidence to green.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import statistics
import sys
from pathlib import Path
from typing import Any, Iterable

SHA_RE = re.compile(r"^[0-9a-f]{40}$")
DECISIONS = {"pass", "regression", "unstable", "insufficient-data", "invalid"}
BLOCKING_DECISIONS = {"regression", "unstable", "insufficient-data", "invalid"}


class ComparatorError(ValueError):
    def __init__(self, reason_code: str, message: str) -> None:
        super().__init__(message)
        self.reason_code = reason_code


def _load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ComparatorError("invalid-json", f"cannot read JSON {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ComparatorError("invalid-json-root", f"{path} must contain a JSON object")
    return value


def _require_sha(value: Any, field: str) -> str:
    if not isinstance(value, str) or not SHA_RE.fullmatch(value):
        raise ComparatorError("invalid-sha", f"{field} must be a full lowercase 40-character SHA")
    return value


def _require_nonempty(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ComparatorError("missing-field", f"{field} must be non-empty")
    return value.strip()


def _finite_samples(value: Any, field: str) -> list[float]:
    if not isinstance(value, list):
        raise ComparatorError("invalid-samples", f"{field} must be an array")
    samples: list[float] = []
    for index, item in enumerate(value):
        if not isinstance(item, (int, float)) or isinstance(item, bool) or not math.isfinite(float(item)):
            raise ComparatorError("invalid-samples", f"{field}[{index}] must be a finite number")
        number = float(item)
        if number < 0:
            raise ComparatorError("invalid-samples", f"{field}[{index}] must be non-negative")
        samples.append(number)
    return samples


def _percentile(sorted_samples: list[float], percentile: float) -> float:
    if not sorted_samples:
        raise ComparatorError("invalid-samples", "cannot compute a percentile for an empty sample set")
    if len(sorted_samples) == 1:
        return sorted_samples[0]
    rank = (len(sorted_samples) - 1) * percentile
    lower = math.floor(rank)
    upper = math.ceil(rank)
    if lower == upper:
        return sorted_samples[lower]
    weight = rank - lower
    return sorted_samples[lower] * (1.0 - weight) + sorted_samples[upper] * weight


def summarize(samples: Iterable[float]) -> dict[str, float | None]:
    ordered = sorted(float(value) for value in samples)
    if not ordered:
        raise ComparatorError("invalid-samples", "at least one measured sample is required")
    median = statistics.median(ordered)
    absolute_deviations = [abs(value - median) for value in ordered]
    mad = statistics.median(absolute_deviations)
    mean = statistics.fmean(ordered)
    stdev = statistics.pstdev(ordered) if len(ordered) > 1 else 0.0
    cv = None if mean == 0 else stdev / abs(mean)
    relative_mad = None if median == 0 else mad / abs(median)
    return {
        "min": ordered[0],
        "max": ordered[-1],
        "median": median,
        "p50": _percentile(ordered, 0.50),
        "p95": _percentile(ordered, 0.95),
        "p99": _percentile(ordered, 0.99),
        "mad": mad,
        "cv": cv,
        "relativeMad": relative_mad,
        "range": ordered[-1] - ordered[0],
    }


def _compatibility_payload(fingerprint: dict[str, Any]) -> dict[str, Any]:
    if fingerprint.get("schemaVersion") != 1 or fingerprint.get("phase") != "environment-fingerprint":
        raise ComparatorError("invalid-environment-fingerprint", "PERF-02 environment fingerprint schema/phase mismatch")
    runner = fingerprint.get("runner")
    dotnet = fingerprint.get("dotnet")
    node = fingerprint.get("node")
    postgresql = fingerprint.get("postgresql")
    browser = fingerprint.get("browser")
    images = fingerprint.get("containerImages")
    fixture = fingerprint.get("fixture")
    for name, value in (
        ("runner", runner), ("dotnet", dotnet), ("node", node), ("postgresql", postgresql),
        ("browser", browser), ("containerImages", images), ("fixture", fixture),
    ):
        if not isinstance(value, dict):
            raise ComparatorError("invalid-environment-fingerprint", f"fingerprint.{name} must be an object")

    required = {
        "runnerOs": runner.get("runnerOs"),
        "runnerImage": runner.get("runnerImage"),
        "cpuModel": runner.get("cpuModel"),
        "cpuCount": runner.get("cpuCount"),
        "dotnetRuntime": dotnet.get("runtimeInfo"),
        "node": node.get("version"),
        "postgresql": postgresql.get("version"),
        "playwright": browser.get("playwrightVersion"),
        "browser": browser.get("version"),
        "appImage": images.get("app"),
        "postgresImage": images.get("postgres"),
        "browserImage": images.get("performanceBrowser"),
        "fixtureProfile": fixture.get("profile"),
        "fixtureHash": fixture.get("hash"),
        "fixtureVersion": fixture.get("version"),
    }
    for key, value in required.items():
        if value is None or value == "" or isinstance(value, bool):
            raise ComparatorError("invalid-environment-fingerprint", f"fingerprint compatibility field {key} is missing")
    if not isinstance(required["cpuCount"], int) or required["cpuCount"] <= 0:
        raise ComparatorError("invalid-environment-fingerprint", "fingerprint runner CPU count must be positive")
    if not isinstance(required["fixtureVersion"], int) or required["fixtureVersion"] <= 0:
        raise ComparatorError("invalid-environment-fingerprint", "fixture version must be positive")
    return required


def environment_compatibility_key(fingerprint: dict[str, Any]) -> str:
    payload = _compatibility_payload(fingerprint)
    canonical = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def _find_scenario_metric(
    scenarios_document: dict[str, Any], scenario_id: str, metric_id: str
) -> tuple[dict[str, Any], dict[str, Any]]:
    if scenarios_document.get("schemaVersion") != 1:
        raise ComparatorError("invalid-contract", "unsupported scenarios schemaVersion")
    scenarios = scenarios_document.get("scenarios")
    if not isinstance(scenarios, list):
        raise ComparatorError("invalid-contract", "scenarios.scenarios must be an array")
    scenario = next((item for item in scenarios if isinstance(item, dict) and item.get("id") == scenario_id), None)
    if scenario is None:
        raise ComparatorError("unknown-scenario", f"unknown scenario: {scenario_id}")
    metrics = scenario.get("metrics")
    if not isinstance(metrics, list):
        raise ComparatorError("invalid-contract", f"scenario {scenario_id} has no metric array")
    metric = next((item for item in metrics if isinstance(item, dict) and item.get("name") == metric_id), None)
    if metric is None:
        raise ComparatorError("unknown-metric", f"scenario {scenario_id} does not declare metric {metric_id}")
    gate = metric.get("gate")
    if gate not in {"hard-ceiling", "relative-regression", "trend-only", "extended-only"}:
        raise ComparatorError("invalid-contract", f"unsupported gate {gate!r}")
    return scenario, metric


def _find_budget(budgets_document: dict[str, Any], scenario_id: str, metric_id: str, gate: str) -> dict[str, Any] | None:
    if budgets_document.get("schemaVersion") != 1:
        raise ComparatorError("invalid-contract", "unsupported budgets schemaVersion")
    budgets = budgets_document.get("budgets")
    if not isinstance(budgets, list):
        raise ComparatorError("invalid-contract", "budgets.budgets must be an array")
    matches = [
        item for item in budgets
        if isinstance(item, dict) and item.get("scenarioId") == scenario_id and item.get("metric") == metric_id
    ]
    if gate in {"hard-ceiling", "relative-regression"}:
        if len(matches) != 1:
            raise ComparatorError("missing-budget", f"blocking metric {scenario_id}/{metric_id} must have exactly one budget")
        if matches[0].get("gate") != gate:
            raise ComparatorError("budget-gate-mismatch", "budget gate does not match scenario gate")
        return matches[0]
    if matches:
        raise ComparatorError("unexpected-budget", "non-blocking metric must not gain an implicit blocking budget")
    return None


def _minimum_samples(environment_document: dict[str, Any]) -> int:
    if environment_document.get("schemaVersion") != 1:
        raise ComparatorError("invalid-contract", "unsupported environment schemaVersion")
    measurement = environment_document.get("measurement")
    if not isinstance(measurement, dict):
        raise ComparatorError("invalid-contract", "environment.measurement is required")
    minimum = measurement.get("minimumSamples")
    if not isinstance(minimum, int) or minimum <= 1:
        raise ComparatorError("invalid-contract", "environment measurement.minimumSamples must be > 1")
    return minimum


def _policy(policy_document: dict[str, Any]) -> dict[str, Any]:
    if policy_document.get("schemaVersion") != 1 or policy_document.get("resultSchemaVersion") != 1:
        raise ComparatorError("invalid-policy", "unsupported comparison policy schema")
    statistics_policy = policy_document.get("statistics")
    variability = policy_document.get("variability")
    rerun = policy_document.get("rerun")
    baseline = policy_document.get("baseline")
    if not all(isinstance(item, dict) for item in (statistics_policy, variability, rerun, baseline)):
        raise ComparatorError("invalid-policy", "comparison policy sections are missing")
    if statistics_policy.get("percentileMethod") != "linear-interpolation":
        raise ComparatorError("invalid-policy", "PERF-03 percentile method must remain linear-interpolation")
    if variability.get("defaultIndicator") != "relative-mad" or variability.get("ratioIndicator") != "range":
        raise ComparatorError("invalid-policy", "PERF-03 variability indicators must remain relative-mad/range")
    default_max = variability.get("defaultRelativeMadMax")
    ratio_max = variability.get("ratioRangeMax")
    if isinstance(default_max, bool) or not isinstance(default_max, (int, float)) or not 0 < float(default_max) < 1:
        raise ComparatorError("invalid-policy", "defaultRelativeMadMax must be within (0,1)")
    if isinstance(ratio_max, bool) or not isinstance(ratio_max, (int, float)) or not 0 < float(ratio_max) <= 1:
        raise ComparatorError("invalid-policy", "ratioRangeMax must be within (0,1]")
    if (
        rerun.get("maxReruns") != 1
        or rerun.get("preserveAttempts") is not True
        or rerun.get("secondAttemptRequiresPreviousArtifact") is not True
    ):
        raise ComparatorError("invalid-policy", "PERF-03 permits exactly one preserved, linked rerun")
    if (
        baseline.get("approvedSourceRef") != "refs/heads/main"
        or baseline.get("rejectHeadAsBaseline") is not True
        or baseline.get("requireEnvironmentCompatibility") is not True
        or baseline.get("requireFixtureCompatibility") is not True
    ):
        raise ComparatorError("invalid-policy", "baseline policy must bind to approved compatible main evidence")
    return policy_document


def _validate_envelope(measurement: dict[str, Any]) -> bool:
    envelope = measurement.get("measurementEnvelope")
    if not isinstance(envelope, dict):
        raise ComparatorError("invalid-measurement-envelope", "measurementEnvelope is required")
    if envelope.get("warmupSamplesExcluded") is not True:
        raise ComparatorError("warmup-samples-included", "warm-up samples must be excluded")
    environment_stable = envelope.get("environmentStable")
    if not isinstance(environment_stable, bool):
        raise ComparatorError("invalid-measurement-envelope", "measurementEnvelope.environmentStable must be boolean")
    if envelope.get("benchmarkExitCode") != 0 or envelope.get("timedOut") is not False:
        raise ComparatorError("benchmark-failed", "benchmark failed or timed out")
    return environment_stable


def _selected_statistic(metric_id: str) -> str:
    if ".p99" in metric_id:
        return "p99"
    if ".p95" in metric_id:
        return "p95"
    if ".p50" in metric_id:
        return "p50"
    return "median"


def _variability(metric_id: str, unit: str, summary: dict[str, float | None], policy: dict[str, Any]) -> tuple[str, float, float]:
    variability = policy["variability"]
    if unit == "ratio" or metric_id.endswith("error_rate"):
        value = float(summary["range"] or 0.0)
        return "range", value, float(variability["ratioRangeMax"])
    relative_mad = summary["relativeMad"]
    if relative_mad is None:
        value = 0.0 if float(summary["mad"] or 0.0) == 0.0 else math.inf
    else:
        value = float(relative_mad)
    return "relative-mad", value, float(variability["defaultRelativeMadMax"])


def _safe_text(value: Any) -> str | None:
    return value.strip() if isinstance(value, str) and value.strip() else None


def _safe_sha(value: Any) -> str | None:
    return value if isinstance(value, str) and SHA_RE.fullmatch(value) else None


def _safe_attempt(value: Any) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) and value in (1, 2) else None


def _base_result(
    measurement: dict[str, Any] | None,
    baseline: dict[str, Any] | None,
    *,
    decision: str,
    reason_code: str,
    message: str | None = None,
) -> dict[str, Any]:
    raw_attempt = measurement.get("attempt", 1) if isinstance(measurement, dict) else None
    result = {
        "schemaVersion": 1,
        "scenario": _safe_text(measurement.get("scenario")) if isinstance(measurement, dict) else None,
        "metric": _safe_text(measurement.get("metric")) if isinstance(measurement, dict) else None,
        "gate": None,
        "unit": _safe_text(measurement.get("unit")) if isinstance(measurement, dict) else None,
        "headSha": _safe_sha(measurement.get("headSha")) if isinstance(measurement, dict) else None,
        "baselineSha": _safe_sha(baseline.get("baselineSha")) if isinstance(baseline, dict) else None,
        "sampleCount": len(measurement.get("samples", [])) if isinstance(measurement, dict) and isinstance(measurement.get("samples"), list) else 0,
        "summary": None,
        "baseline": None,
        "currentValue": None,
        "baselineValue": None,
        "absoluteDelta": None,
        "relativeDelta": None,
        "budget": None,
        "decision": decision,
        "reasonCode": reason_code,
        "message": message,
        "attempt": _safe_attempt(raw_attempt),
        "rerun": {"allowed": False, "maxReruns": 1, "remaining": 0, "preserveAttempts": True},
        "environmentCompatibilityKey": None,
        "fixture": None,
    }
    if decision not in DECISIONS:
        raise AssertionError(f"unsupported decision {decision}")
    return result


def compare_documents(
    measurement: dict[str, Any],
    baseline: dict[str, Any],
    fingerprint: dict[str, Any],
    scenarios_document: dict[str, Any],
    budgets_document: dict[str, Any],
    environment_document: dict[str, Any],
    policy_document: dict[str, Any],
) -> dict[str, Any]:
    try:
        policy = _policy(policy_document)
        if measurement.get("schemaVersion") != 1:
            raise ComparatorError("measurement-schema-mismatch", "measurement.schemaVersion must be 1")
        scenario_id = _require_nonempty(measurement.get("scenario"), "measurement.scenario")
        metric_id = _require_nonempty(measurement.get("metric"), "measurement.metric")
        head_sha = _require_sha(measurement.get("headSha"), "measurement.headSha")
        unit = _require_nonempty(measurement.get("unit"), "measurement.unit")
        attempt = measurement.get("attempt", 1)
        if isinstance(attempt, bool) or not isinstance(attempt, int) or attempt not in (1, 2):
            raise ComparatorError("rerun-limit-exceeded", "attempt must be 1 or the single permitted rerun attempt 2")
        if attempt == 2:
            _require_nonempty(measurement.get("previousAttemptArtifact"), "measurement.previousAttemptArtifact")
        environment_stable = _validate_envelope(measurement)

        _, metric_contract = _find_scenario_metric(scenarios_document, scenario_id, metric_id)
        gate = str(metric_contract["gate"])
        budget = _find_budget(budgets_document, scenario_id, metric_id, gate)
        current_samples = _finite_samples(measurement.get("samples"), "measurement.samples")
        baseline_samples = _finite_samples(baseline.get("samples"), "baseline.samples")
        minimum = _minimum_samples(environment_document)

        if baseline.get("schemaVersion") != 1 or baseline.get("resultSchemaVersion") != 1:
            raise ComparatorError("baseline-schema-mismatch", "baseline artifact schema/result version mismatch")
        if baseline.get("scenario") != scenario_id or baseline.get("metric") != metric_id or baseline.get("unit") != unit:
            raise ComparatorError("baseline-identity-mismatch", "baseline scenario/metric/unit does not match current measurement")
        baseline_sha = _require_sha(baseline.get("baselineSha"), "baseline.baselineSha")
        if baseline_sha == head_sha:
            raise ComparatorError("pr-head-baseline-rejected", "current head must never become its own baseline")
        if baseline.get("sourceRef") != policy["baseline"]["approvedSourceRef"] or baseline.get("approved") is not True:
            raise ComparatorError("unapproved-baseline", "baseline must be approved and sourced from refs/heads/main")
        if budget is not None:
            budget_baseline = budget.get("baseline")
            if not isinstance(budget_baseline, dict):
                raise ComparatorError("invalid-contract", "blocking budget baseline metadata is missing")
            if budget_baseline.get("sha") != baseline_sha:
                raise ComparatorError("baseline-sha-mismatch", "baseline artifact SHA does not match PERF-01 budget baseline SHA")

        fingerprint_sha = _require_sha(fingerprint.get("commitSha"), "fingerprint.commitSha")
        if fingerprint_sha != head_sha:
            raise ComparatorError("environment-head-mismatch", "PERF-02 fingerprint commit SHA does not match measurement head SHA")
        current_key = environment_compatibility_key(fingerprint)
        baseline_key = _require_nonempty(baseline.get("environmentCompatibilityKey"), "baseline.environmentCompatibilityKey")
        if baseline_key != current_key:
            raise ComparatorError("environment-fingerprint-mismatch", "baseline and current PERF-02 environments are not comparable")
        fixture = fingerprint["fixture"]
        if baseline.get("fixtureHash") != fixture.get("hash") or baseline.get("fixtureVersion") != fixture.get("version"):
            raise ComparatorError("fixture-mismatch", "baseline fixture hash/version does not match current PERF-02 fixture")

        result = _base_result(measurement, baseline, decision="pass", reason_code="within-policy")
        result.update({
            "gate": gate,
            "headSha": head_sha,
            "baselineSha": baseline_sha,
            "attempt": attempt,
            "environmentCompatibilityKey": current_key,
            "fixture": {"profile": fixture.get("profile"), "hash": fixture.get("hash"), "version": fixture.get("version")},
        })

        if len(current_samples) < minimum or len(baseline_samples) < minimum:
            result["decision"] = "insufficient-data"
            result["reasonCode"] = "insufficient-samples"
            result["message"] = f"current/baseline samples must each contain at least {minimum} values"
            return result

        if not environment_stable:
            result["decision"] = "unstable"
            result["reasonCode"] = "environment-unstable"
            result["message"] = "PERF-02 marked the current measurement environment unstable"
            if attempt == 1:
                result["rerun"] = {"allowed": True, "maxReruns": 1, "remaining": 1, "preserveAttempts": True}
            return result

        current_summary = summarize(current_samples)
        baseline_summary = summarize(baseline_samples)
        current_kind, current_variability, current_limit = _variability(metric_id, unit, current_summary, policy)
        baseline_kind, baseline_variability, baseline_limit = _variability(metric_id, unit, baseline_summary, policy)
        current_summary["variabilityKind"] = current_kind
        current_summary["variabilityValue"] = current_variability
        current_summary["variabilityLimit"] = current_limit
        baseline_summary["variabilityKind"] = baseline_kind
        baseline_summary["variabilityValue"] = baseline_variability
        baseline_summary["variabilityLimit"] = baseline_limit
        result["sampleCount"] = len(current_samples)
        result["summary"] = current_summary
        result["baseline"] = {"sampleCount": len(baseline_samples), "summary": baseline_summary}

        if current_variability > current_limit or baseline_variability > baseline_limit:
            result["decision"] = "unstable"
            result["reasonCode"] = "high-variability"
            result["message"] = "current or baseline variability exceeds the versioned PERF-03 policy"
            if attempt == 1:
                result["rerun"] = {"allowed": True, "maxReruns": 1, "remaining": 1, "preserveAttempts": True}
            return result

        statistic = _selected_statistic(metric_id)
        current_value = float(current_summary[statistic])
        baseline_value = float(baseline_summary[statistic])
        absolute_delta = current_value - baseline_value
        relative_delta = None if baseline_value == 0 else absolute_delta / abs(baseline_value)
        result["currentValue"] = current_value
        result["baselineValue"] = baseline_value
        result["absoluteDelta"] = absolute_delta
        result["relativeDelta"] = relative_delta

        if gate == "hard-ceiling":
            assert budget is not None
            limit = budget.get("limit")
            if not isinstance(limit, dict) or limit.get("unit") != unit:
                raise ComparatorError("budget-unit-mismatch", "hard-ceiling budget unit does not match measurement unit")
            limit_value = limit.get("value")
            if not isinstance(limit_value, (int, float)) or isinstance(limit_value, bool) or not math.isfinite(float(limit_value)):
                raise ComparatorError("invalid-contract", "hard-ceiling limit must be finite")
            result["budget"] = {"id": budget.get("id"), "gate": gate, "limit": float(limit_value), "unit": unit}
            if current_value > float(limit_value):
                result["decision"] = "regression"
                result["reasonCode"] = "hard-ceiling-exceeded"
            else:
                result["reasonCode"] = "hard-ceiling-satisfied"
        elif gate == "relative-regression":
            assert budget is not None
            comparison = budget.get("comparison")
            if not isinstance(comparison, dict):
                raise ComparatorError("invalid-contract", "relative budget comparison is missing")
            max_increase = comparison.get("maxIncreasePercent")
            if not isinstance(max_increase, (int, float)) or isinstance(max_increase, bool) or not math.isfinite(float(max_increase)):
                raise ComparatorError("invalid-contract", "relative budget maxIncreasePercent must be finite")
            if baseline_value == 0:
                raise ComparatorError("zero-baseline-relative-comparison", "relative regression cannot use a zero baseline statistic")
            threshold = float(max_increase) / 100.0
            result["budget"] = {
                "id": budget.get("id"), "gate": gate, "maxIncreasePercent": float(max_increase), "unit": unit
            }
            if relative_delta is not None and relative_delta > threshold:
                result["decision"] = "regression"
                result["reasonCode"] = "relative-budget-exceeded"
            else:
                result["reasonCode"] = "relative-budget-satisfied"
        elif gate == "trend-only":
            result["reasonCode"] = "trend-recorded"
        else:
            result["reasonCode"] = "extended-result-recorded"

        return result
    except ComparatorError as exc:
        return _base_result(measurement, baseline, decision="invalid", reason_code=exc.reason_code, message=str(exc))


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description="Compare PERF-03 performance samples against an approved baseline")
    parser.add_argument("--measurement", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--environment-fingerprint", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--scenarios", type=Path, default=root / "performance" / "scenarios.json")
    parser.add_argument("--budgets", type=Path, default=root / "performance" / "budgets.json")
    parser.add_argument("--environment-contract", type=Path, default=root / "performance" / "environment.json")
    parser.add_argument("--policy", type=Path, default=root / "performance" / "comparison-policy.json")
    args = parser.parse_args()

    measurement: dict[str, Any] | None = None
    baseline: dict[str, Any] | None = None
    try:
        measurement = _load_json(args.measurement)
        baseline = _load_json(args.baseline)
        fingerprint = _load_json(args.environment_fingerprint)
        scenarios = _load_json(args.scenarios)
        budgets = _load_json(args.budgets)
        environment = _load_json(args.environment_contract)
        policy = _load_json(args.policy)
        result = compare_documents(measurement, baseline, fingerprint, scenarios, budgets, environment, policy)
    except ComparatorError as exc:
        result = _base_result(measurement, baseline, decision="invalid", reason_code=exc.reason_code, message=str(exc))

    encoded = json.dumps(result, ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded, encoding="utf-8")
    else:
        sys.stdout.write(encoded)

    if result["decision"] == "pass":
        return 0
    if result["decision"] == "invalid":
        return 2
    return 1


def compare_api_documents(
    measurement: dict[str, Any], baseline: dict[str, Any] | None,
    fingerprint: dict[str, Any], contract: dict[str, Any], policy_document: dict[str, Any],
) -> dict[str, Any]:
    """PERF-04 adapter entry point; decisions remain owned by PERF-03.

    PR absolute gates need no fabricated measured baseline. Main compares the
    medians of independent run-level k6 percentiles/rates, not percentiles of
    pooled requests or copies of a single sample. Every run must have enough
    measured requests, and every run must satisfy the hard ceiling.
    """
    result = _base_result(measurement, baseline, decision="invalid", reason_code="invalid-api-evidence")
    try:
        policy = _policy(policy_document)
        if measurement.get("schemaVersion") != 1:
            raise ComparatorError("measurement-schema-mismatch", "API measurement schema must be 1")
        scenario = _require_nonempty(measurement.get("scenario"), "scenario")
        metric = _require_nonempty(measurement.get("metric"), "metric")
        if contract.get("schemaVersion") != 1 or scenario not in {s["id"] for s in contract["scenarios"]}:
            raise ComparatorError("unknown-scenario", "API scenario is not in the versioned contract")
        metric_policy = contract["metrics"].get(metric)
        if not isinstance(metric_policy, dict) or measurement.get("unit") != metric_policy.get("unit"):
            raise ComparatorError("unknown-metric", "API metric/unit is not in the versioned contract")
        head = _require_sha(measurement.get("headSha"), "headSha")
        if fingerprint.get("commitSha") != head:
            raise ComparatorError("environment-head-mismatch", "fingerprint SHA must equal measurement SHA")
        key = environment_compatibility_key(fingerprint)
        if fingerprint.get("k6Version") != contract.get("k6Version"):
            raise ComparatorError("toolchain-mismatch", "k6 version does not match the pinned contract")
        stable = _validate_envelope(measurement)
        mode = measurement.get("mode")
        if mode not in {"fast", "regression"}:
            raise ComparatorError("invalid-mode", "API mode must be fast or regression")
        samples = _finite_samples(measurement.get("samples"), "samples")
        counts = measurement.get("requestCounts")
        if not isinstance(counts, list) or len(counts) != len(samples):
            raise ComparatorError("invalid-samples", "request counts must accompany every run")
        minimum = 1 if mode == "fast" else contract["mainRuns"]
        result.update({
            "gate": "hard-ceiling" if "ceiling" in metric_policy else "trend-only",
            "unit": metric_policy["unit"], "headSha": head,
            "environmentCompatibilityKey": key,
            "fixture": {"profile": fingerprint["fixture"]["profile"],
                        "hash": fingerprint["fixture"]["hash"], "version": fingerprint["fixture"]["version"]},
            "budget": dict(metric_policy), "attempt": 1,
        })
        if len(samples) < minimum or any(isinstance(c, bool) or not isinstance(c, int) or
                                         c < contract["minimumRequests"] for c in counts):
            result.update(decision="insufficient-data", reasonCode="insufficient-samples")
            return result
        summary = summarize(samples)
        kind, variability, limit = _variability(metric, metric_policy["unit"], summary, policy)
        summary.update(variabilityKind=kind, variabilityValue=variability, variabilityLimit=limit)
        result.update(summary=summary, currentValue=summary["median"])
        if not stable:
            result.update(decision="unstable", reasonCode="environment-unstable")
            return result
        # Hard failures take precedence over noise. A single 5xx/timeout cannot
        # disappear in a median error rate, and slow runs cannot be averaged out.
        if "ceiling" in metric_policy and max(samples) > metric_policy["ceiling"]:
            result.update(decision="regression", reasonCode="hard-ceiling-exceeded")
            return result
        if mode == "fast":
            result.update(decision="pass", reasonCode="absolute-api-budget-satisfied")
            return result
        if not isinstance(baseline, dict):
            raise ComparatorError("missing-baseline", "main API comparison requires a measured approved-main baseline")
        baseline_sha = _require_sha(baseline.get("baselineSha"), "baselineSha")
        if baseline_sha != contract["baseline"]["sha"] or baseline_sha == head:
            raise ComparatorError("baseline-sha-mismatch", "baseline must match the reviewed main SHA and differ from head")
        if baseline.get("sourceRef") != "refs/heads/main" or baseline.get("approved") is not True:
            raise ComparatorError("unapproved-baseline", "baseline must identify approved main source")
        if (baseline.get("scenario") != scenario or baseline.get("metric") != metric or
                baseline.get("unit") != metric_policy["unit"]):
            raise ComparatorError("baseline-identity-mismatch", "baseline scenario/metric/unit mismatch")
        if baseline.get("environmentCompatibilityKey") != key or baseline.get("k6Version") != contract["k6Version"]:
            raise ComparatorError("environment-fingerprint-mismatch", "baseline environment/toolchain mismatch")
        if (baseline.get("fixtureHash") != fingerprint["fixture"]["hash"] or
                baseline.get("fixtureVersion") != fingerprint["fixture"]["version"]):
            raise ComparatorError("fixture-mismatch", "baseline fixture mismatch")
        baseline_samples = _finite_samples(baseline.get("samples"), "baseline.samples")
        baseline_counts = baseline.get("requestCounts")
        if (len(baseline_samples) < minimum or not isinstance(baseline_counts, list) or
                len(baseline_counts) != len(baseline_samples) or any(
                    isinstance(c, bool) or not isinstance(c, int) or c < contract["minimumRequests"] for c in baseline_counts)):
            result.update(decision="insufficient-data", reasonCode="insufficient-baseline-samples")
            return result
        baseline_summary = summarize(baseline_samples)
        bkind, bvar, blimit = _variability(metric, metric_policy["unit"], baseline_summary, policy)
        baseline_summary.update(variabilityKind=bkind, variabilityValue=bvar, variabilityLimit=blimit)
        current_value = float(summary["median"])
        baseline_value = float(baseline_summary["median"])
        delta = current_value - baseline_value
        relative = delta / baseline_value if baseline_value else None
        result.update(baselineSha=baseline_sha, baseline={"sampleCount": len(baseline_samples), "summary": baseline_summary},
                      baselineValue=baseline_value, absoluteDelta=delta, relativeDelta=relative)
        if variability > limit or bvar > blimit:
            result.update(decision="unstable", reasonCode="high-variability")
            return result
        if "maxIncreasePercent" in metric_policy or "maxDecreasePercent" in metric_policy:
            result["gate"] = "relative-regression"
            if baseline_value <= 0:
                raise ComparatorError("zero-baseline-relative-comparison", "relative API comparison requires a positive baseline")
            increase = ("maxIncreasePercent" in metric_policy and
                        relative * 100 > metric_policy["maxIncreasePercent"] and
                        delta > metric_policy["minimumAbsoluteIncrease"])
            decrease = ("maxDecreasePercent" in metric_policy and
                        relative * 100 < -metric_policy["maxDecreasePercent"])
            if increase or decrease:
                result.update(decision="regression", reasonCode="relative-budget-exceeded")
                return result
        result.update(decision="pass", reasonCode="api-comparison-satisfied")
        return result
    except (ComparatorError, KeyError, TypeError, ValueError) as exc:
        result.update(decision="invalid", reasonCode=getattr(exc, "reason_code", "invalid-api-contract"), message=str(exc))
        return result


if __name__ == "__main__":
    raise SystemExit(main())
