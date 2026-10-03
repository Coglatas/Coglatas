#!/usr/bin/env python3
"""PERF-05 structural DB evidence validator. SQL/body/parameters never enter its output."""
from __future__ import annotations

import math
import re
import statistics
from collections import Counter
from typing import Any

from common import PerformanceContractError

COMMAND_FIELDS = {"fingerprint", "durationMs", "readOperations", "failed", "rootTable", "bounded", "ordered"}
CAPTURE_FIELDS = {"schemaVersion", "status", "commandCount", "totalDurationMs", "commands", "slowestCommands"}
TABLES = {"task_items", "tasks", "projects", "workspaces", "attachments", "file_objects", "conversations", "messages", "notifications", "announcements"}


def number(value: Any, label: str, *, integer: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise PerformanceContractError(f"invalid numeric {label}")
    if integer and not isinstance(value, int):
        raise PerformanceContractError(f"invalid integer {label}")
    return value


def validate_contract(contract: dict, inventory: dict) -> None:
    if set(contract) != {"schemaVersion", "inventorySha", "policy", "scenarios", "planChecks"} or contract["schemaVersion"] != 1:
        raise PerformanceContractError("invalid PERF-05 contract schema")
    if not re.fullmatch(r"[0-9a-f]{40}", contract["inventorySha"]):
        raise PerformanceContractError("missing inventory SHA")
    policy = contract["policy"]
    for key in ("queryCountHardCeiling", "maximumFixedPageQueryGrowth", "maximumPageSizeQueryGrowth", "slowCommandHardCeilingMs", "samples"):
        number(policy[key], key, integer=True)
    if not 1 <= policy["queryCountHardCeiling"] <= 100 or not 1000 <= policy["slowCommandHardCeilingMs"] <= 10000:
        raise PerformanceContractError("disabled or invalid hard ceiling")
    if policy["samples"] < 5 or policy["pageSizes"] != [5, 10]:
        raise PerformanceContractError("invalid sample/page-size policy")
    if any(not 0 <= policy[key] <= 2 for key in ("maximumFixedPageQueryGrowth", "maximumPageSizeQueryGrowth")):
        raise PerformanceContractError("disabled growth gate")
    if not all(policy.get(key) for key in ("rationale", "evidence", "durationPolicy")):
        raise PerformanceContractError("missing policy rationale")
    expected = {s["id"] for s in inventory["scenarios"] if any(x["kind"] == "api" for x in s["surfaces"]) and s["id"].endswith((".list", ".messages", ".my-tasks"))}
    scenarios = contract["scenarios"]
    if len({s["id"] for s in scenarios}) != len(scenarios) or {s["id"] for s in scenarios} != expected:
        raise PerformanceContractError("duplicate or incomplete major-list inventory")
    for scenario in scenarios:
        source = next(s for s in inventory["scenarios"] if s["id"] == scenario["id"])
        template = scenario["path"].split("?")[0].replace("{taskListProjectId}", "{projectId}")
        if not any(surface.get("path") == template for surface in source["surfaces"]):
            raise PerformanceContractError("scenario path not backed by PERF-01 inventory")
        if scenario["rootTable"] not in TABLES or type(scenario["paged"]) is not bool:
            raise PerformanceContractError("invalid pagination contract")
    if not contract["planChecks"]:
        raise PerformanceContractError("missing selected plan invariant")


def validate_capture(raw: dict) -> dict:
    # Reject extra fields recursively before persisting anything received from the app.
    # This is stronger than trying to redact secrets from arbitrary SQL/error/log text.
    if set(raw) != CAPTURE_FIELDS or raw.get("schemaVersion") != 1 or type(raw.get("status")) is not int:
        raise PerformanceContractError("unsafe or invalid capture fields")
    count = number(raw["commandCount"], "commandCount", integer=True)
    total = number(raw["totalDurationMs"], "totalDurationMs")
    commands = raw["commands"]
    if not isinstance(commands, list) or count != len(commands) or not count:
        raise PerformanceContractError("instrumentation empty or inconsistent")
    if not isinstance(raw["slowestCommands"], list) or len(raw["slowestCommands"]) > 5:
        raise PerformanceContractError("invalid slow command evidence")
    for command in commands + raw["slowestCommands"]:
        if not isinstance(command, dict) or set(command) != COMMAND_FIELDS:
            raise PerformanceContractError("unsafe command fields")
        if not isinstance(command["fingerprint"], str) or not re.fullmatch(r"[0-9a-f]{64}", command["fingerprint"]):
            raise PerformanceContractError("invalid SQL identity")
        number(command["durationMs"], "durationMs")
        if command["readOperations"] is not None:
            number(command["readOperations"], "readOperations", integer=True)
        if command["rootTable"] is not None and command["rootTable"] not in TABLES:
            raise PerformanceContractError("unsafe table identity")
        if any(type(command[field]) is not bool for field in ("failed", "bounded", "ordered")):
            raise PerformanceContractError("invalid structural flags")
    if not math.isclose(total, sum(c["durationMs"] for c in commands), rel_tol=1e-8, abs_tol=1e-8):
        raise PerformanceContractError("inconsistent DB duration")
    expected_slowest = sorted(commands, key=lambda c: c["durationMs"], reverse=True)[:5]
    if raw["slowestCommands"] != expected_slowest:
        raise PerformanceContractError("inconsistent slow command evidence")
    return raw


def capture_failures(capture: dict, scenario: dict, page_size: int, policy: dict) -> list[str]:
    validate_capture(capture)
    failures = []
    if capture["status"] != 200 or any(c["failed"] for c in capture["commands"]):
        failures.append("scenario-or-command-failed")
    if capture["commandCount"] > policy["queryCountHardCeiling"]:
        failures.append("query-count-hard-ceiling")
    if max(c["durationMs"] for c in capture["commands"]) > policy["slowCommandHardCeilingMs"]:
        failures.append("extreme-slow-command")
    if scenario["paged"]:
        rows = [c for c in capture["commands"] if c["rootTable"] == scenario["rootTable"] and c["bounded"] and c["ordered"]]
        if not rows:
            failures.append("missing-ordered-db-page")
        # A final false Read attempt is counted by EF; it is not a returned row.
        # Batch joins/lookups have a different root table and are not page readers.
        if any(c["readOperations"] is not None and c["readOperations"] > page_size + 1 for c in rows):
            failures.append("over-materialized-page")
        unbounded = [c for c in capture["commands"] if c["rootTable"] == scenario["rootTable"] and c["readOperations"] is not None and c["readOperations"] > page_size + 1 and not c["bounded"]]
        if unbounded:
            failures.append("unbounded-collection-materialization")
    return failures


def growth_failures(small: dict, medium: dict, policy: dict) -> list[str]:
    if medium["cardinality"] <= small["cardinality"]:
        raise PerformanceContractError("fixture cardinality did not grow")
    failures = []
    # Compare repeated medians; high baseline outliers cannot hide repeated N+1.
    for page_size in policy["pageSizes"]:
        a = [s["capture"]["commandCount"] for s in small["samples"] if s["pageSize"] == page_size and s["page"] == 1]
        b = [s["capture"]["commandCount"] for s in medium["samples"] if s["pageSize"] == page_size and s["page"] == 1]
        if min(len(a), len(b)) < policy["samples"]:
            raise PerformanceContractError("missing repeated growth samples")
        if any(max(values) - min(values) > policy["maximumFixedPageQueryGrowth"] for values in (a, b)):
            failures.append("unstable-query-count")
        if statistics.median(b) - statistics.median(a) > policy["maximumFixedPageQueryGrowth"]:
            failures.append("n-plus-one-cardinality-growth")
    for profile in (small, medium):
        counts = {size: statistics.median(s["capture"]["commandCount"] for s in profile["samples"] if s["pageSize"] == size and s["page"] == 1) for size in policy["pageSizes"]}
        if counts[10] - counts[5] > policy["maximumPageSizeQueryGrowth"]:
            failures.append("n-plus-one-page-size-growth")
        # Fingerprints expose repeated command families without SQL text.
    return sorted(set(failures))


def plan_invariant(plan: Any, relation: str, required_index: str) -> bool:
    if isinstance(plan, list):
        return any(plan_invariant(item, relation, required_index) for item in plan)
    if not isinstance(plan, dict):
        return False
    if plan.get("Relation Name") == relation and plan.get("Index Name") == required_index and plan.get("Node Type") in {"Index Scan", "Index Only Scan", "Bitmap Index Scan"}:
        return True
    # Bitmap Index Scan omits the relation, which is supplied by its heap parent.
    if plan.get("Relation Name") == relation and plan.get("Node Type") == "Bitmap Heap Scan":
        return any(child.get("Node Type") == "Bitmap Index Scan" and child.get("Index Name") == required_index for child in plan.get("Plans", []))
    return any(plan_invariant(value, relation, required_index) for key, value in plan.items() if key in {"Plan", "Plans"})


def fingerprint_counts(capture: dict) -> dict[str, int]:
    return dict(sorted(Counter(c["fingerprint"] for c in capture["commands"]).items()))
