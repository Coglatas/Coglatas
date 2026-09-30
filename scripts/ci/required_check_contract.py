#!/usr/bin/env python3
"""GOV-06 required-check contract helpers."""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parents[2]
POLICY_PATH = ROOT / "governance/policy.json"
REGISTRY_PATH = ROOT / "governance/required-checks.json"
KINDS = {"workflow-job", "commit-status"}
SCOPES = {"all-pr", "sensitive-only", "release"}
PENDING = {"queued", "in_progress", "pending"}
REJECTED = {"failure", "timed_out", "action_required", "cancelled", "skipped", "neutral", "stale", "error"}
RUN_URL = re.compile(r"/actions/runs/(?P<id>\d+)(?:/|$)")


def _load(path: Path, label: str) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"unable to load {label}: {exc}") from exc


def _control(policy: dict[str, Any], cid: str, family: str) -> dict[str, Any]:
    controls = policy.get("controls")
    found = [c for c in controls if isinstance(c, dict) and c.get("id") == cid] if isinstance(controls, list) else []
    if len(found) != 1 or found[0].get("family") != family:
        raise RuntimeError(f"policy must define exactly one {cid} / {family} control")
    return found[0]


def _text(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value:
        raise RuntimeError(f"{label} must be a non-empty string")
    return value


def load_required_status_checks(policy_path: Path = POLICY_PATH) -> tuple[dict[str, Any], ...]:
    policy = _load(policy_path, "governance policy")
    control = _control(policy, "GOV-CHECKS-001", "required-status-checks")
    required = control.get("expected", {}).get("required")
    if not isinstance(required, list) or not required:
        raise RuntimeError("GOV-CHECKS-001.expected.required must be non-empty")
    result, contexts = [], set()
    for i, item in enumerate(required):
        if not isinstance(item, dict) or set(item) != {"kind", "workflow", "job", "context"}:
            raise RuntimeError(f"GOV-CHECKS-001.expected.required[{i}] is invalid")
        normalized = {k: _text(item.get(k), f"required check [{i}].{k}") for k in ("kind", "workflow", "job", "context")}
        if normalized["kind"] not in KINDS or normalized["context"] in contexts:
            raise RuntimeError(f"required check [{i}] kind/context is invalid or duplicate")
        contexts.add(normalized["context"])
        result.append(normalized)
    return tuple(result)


def _validate_rename(value: Any, label: str) -> None:
    fields = {"state", "previous_context", "previous_workflow", "previous_job", "migration_issue"}
    if not isinstance(value, dict) or set(value) != fields or value["state"] not in {"stable", "dual-publish"}:
        raise RuntimeError(f"{label} is invalid")
    previous = [value[k] for k in ("previous_context", "previous_workflow", "previous_job")]
    if any(v is not None and (not isinstance(v, str) or not v) for v in previous):
        raise RuntimeError(f"{label} previous identifiers are invalid")
    issue = value["migration_issue"]
    if issue is not None and (not isinstance(issue, int) or isinstance(issue, bool) or issue <= 0):
        raise RuntimeError(f"{label}.migration_issue is invalid")
    if value["state"] == "stable" and (any(v is not None for v in previous) or issue is not None):
        raise RuntimeError(f"{label}: stable state cannot retain migration metadata")
    if value["state"] == "dual-publish" and (not any(v is not None for v in previous) or issue is None):
        raise RuntimeError(f"{label}: dual-publish requires previous identifier + migration_issue")


def _validate_trigger(item: dict[str, Any], label: str) -> None:
    trigger = item["trigger"]
    if item["kind"] == "workflow-job":
        if trigger != {"mode": "unfiltered-pull-request", "event": "pull_request"}:
            raise RuntimeError(f"{label}: workflow-job trigger contract is invalid")
        return
    if not isinstance(trigger, dict) or set(trigger) != {"mode", "events", "source_workflow"}:
        raise RuntimeError(f"{label}: commit-status trigger fields are invalid")
    events = trigger.get("events")
    if trigger.get("mode") != "trusted-default-branch" or not isinstance(events, list) or not events or len(events) != len(set(events)):
        raise RuntimeError(f"{label}: commit-status trigger is invalid")
    if "workflow_run" not in events or any(e not in {"workflow_run", "workflow_dispatch"} for e in events):
        raise RuntimeError(f"{label}: commit-status trusted events are invalid")
    _text(trigger.get("source_workflow"), f"{label}.trigger.source_workflow")


def load_required_check_registry(registry_path: Path = REGISTRY_PATH, policy_path: Path = POLICY_PATH) -> dict[str, Any]:
    registry = _load(registry_path, "required-check registry")
    if not isinstance(registry, dict) or set(registry) != {"$schema", "version", "policy_control_id", "ruleset", "checks"}:
        raise RuntimeError("required-check registry top-level fields are invalid")
    if registry["$schema"] != "./required-checks.schema.json" or registry["version"] != 1 or registry["policy_control_id"] != "GOV-CHECKS-001":
        raise RuntimeError("required-check registry identity is invalid")
    ruleset = registry["ruleset"]
    if not isinstance(ruleset, dict) or ruleset.get("enforcement") != "active" or ruleset.get("strict_required_status_checks_policy") is not True:
        raise RuntimeError("required-check registry requires active + strict ruleset")
    _text(ruleset.get("name"), "registry ruleset.name")
    checks = registry["checks"]
    if not isinstance(checks, list) or not checks:
        raise RuntimeError("required-check registry checks must be non-empty")
    fields = {"gate_id", "kind", "workflow", "job", "context", "scope", "trigger", "producer", "ruleset_integration_id", "allowed_conclusions", "timeout_minutes", "staleness_policy", "rename"}
    ids, contexts, projection = set(), set(), []
    for i, item in enumerate(checks):
        label = f"registry checks[{i}]"
        if not isinstance(item, dict) or set(item) != fields:
            raise RuntimeError(f"{label} fields are invalid")
        gate = _text(item["gate_id"], f"{label}.gate_id")
        if not re.fullmatch(r"GOV-GATE-[A-Z0-9-]+-\d{3}", gate) or gate in ids:
            raise RuntimeError(f"{label}.gate_id is invalid or duplicate")
        ids.add(gate)
        for key in ("workflow", "job", "context"):
            _text(item[key], f"{label}.{key}")
        if item["kind"] not in KINDS or item["scope"] not in SCOPES or item["context"] in contexts:
            raise RuntimeError(f"{label} kind/scope/context is invalid or duplicate")
        contexts.add(item["context"])
        _validate_trigger(item, label)
        _validate_rename(item["rename"], f"{label}.rename")
        producer, integration = item["producer"], item["ruleset_integration_id"]
        if item["kind"] == "workflow-job":
            if not isinstance(producer, dict) or set(producer) != {"type", "integration_id", "app_slug"} or producer.get("type") != "github-actions-check" or producer.get("app_slug") != "github-actions":
                raise RuntimeError(f"{label}: workflow-job producer is invalid")
            if integration != producer.get("integration_id") or not isinstance(integration, int) or integration <= 0:
                raise RuntimeError(f"{label}: ruleset integration is invalid")
        else:
            if not isinstance(producer, dict) or set(producer) != {"type", "creator_login", "workflow_path_required"} or producer.get("type") != "github-actions-status" or producer.get("workflow_path_required") is not True or integration is not None:
                raise RuntimeError(f"{label}: commit-status producer is invalid")
            _text(producer.get("creator_login"), f"{label}.producer.creator_login")
        if item["allowed_conclusions"] != ["success"] or not isinstance(item["timeout_minutes"], int) or item["timeout_minutes"] <= 0 or item["staleness_policy"] != "exact-head-and-timeout":
            raise RuntimeError(f"{label}: conclusion/timeout/staleness policy is invalid")
        projection.append({k: item[k] for k in ("kind", "workflow", "job", "context")})
    if tuple(projection) != load_required_status_checks(policy_path):
        raise RuntimeError("required-check registry projection must exactly match GOV-CHECKS-001")
    expected = _control(_load(policy_path, "governance policy"), "GOV-RULESET-001", "ruleset").get("expected", {})
    if ruleset["name"] not in expected.get("ruleset_names", []) or expected.get("strict_required_status_checks") is not True:
        raise RuntimeError("registry ruleset is not owned by strict GOV-RULESET-001")
    _assert_migration_collisions(registry)
    return registry


def expanded_checks(registry: dict[str, Any], scopes: set[str] | None = None) -> list[dict[str, Any]]:
    out = []
    for item in registry["checks"]:
        if scopes is not None and item["scope"] not in scopes:
            continue
        current = dict(item)
        current.update(migration_role="current", source_gate_id=item["gate_id"])
        out.append(current)
        rename = item["rename"]
        if rename["state"] == "dual-publish":
            prev = dict(item)
            prev.update(
                gate_id=f"{item['gate_id']}-PREVIOUS",
                context=rename["previous_context"] or item["context"],
                workflow=rename["previous_workflow"] or item["workflow"],
                job=rename["previous_job"] or item["job"],
                migration_role="previous",
                source_gate_id=item["gate_id"],
            )
            out.append(prev)
    return out


def _assert_migration_collisions(registry: dict[str, Any]) -> None:
    seen: dict[tuple[str, int | None], str] = {}
    for item in expanded_checks(registry, {"all-pr"}):
        key = (item["context"], item["ruleset_integration_id"])
        owner = item["source_gate_id"]
        if key in seen and seen[key] != owner:
            raise RuntimeError(f"migration/live identity collision for {item['context']!r}")
        seen[key] = owner


def _lines(text: str) -> list[str]:
    result = []
    for raw in text.splitlines():
        quote = None
        escaped = False
        out = []
        for ch in raw:
            if escaped:
                out.append(ch)
                escaped = False
            elif ch == "\\" and quote == '"':
                out.append(ch)
                escaped = True
            elif ch in {"'", '"'}:
                quote = None if quote == ch else (ch if quote is None else quote)
                out.append(ch)
            elif ch == "#" and quote is None:
                break
            else:
                out.append(ch)
        result.append("".join(out).rstrip())
    return result


def _indent(line: str) -> int:
    return len(line) - len(line.lstrip())


def event_state(text: str, event: str) -> tuple[bool, bool]:
    lines = _lines(text)
    token = re.compile(rf"(^|[\s,\[])[\"']?{re.escape(event)}[\"']?(?=$|[\s,\]])")
    for oi, line in enumerate(lines):
        m = re.match(r"^(?:on|'on'|\"on\")\s*:\s*(.*)$", line)
        if not m:
            continue
        inline = m.group(1).strip()
        if inline:
            present = bool(token.search(inline))
            return present, present
        base = _indent(line)
        nested = [i for i in range(oi + 1, len(lines)) if lines[i].strip() and _indent(lines[i]) > base]
        if not nested:
            return False, False
        ei = min(_indent(lines[i]) for i in nested)
        pattern = re.compile(rf"^[\"']?{re.escape(event)}[\"']?\s*:\s*(.*)$")
        for i in nested:
            if _indent(lines[i]) != ei:
                continue
            em = pattern.match(lines[i].strip())
            if not em:
                continue
            value = em.group(1).strip()
            if value:
                return True, value in {"{}", "null", "~"}
            for child in lines[i + 1:]:
                if not child.strip():
                    continue
                if _indent(child) <= ei:
                    break
                return True, False
            return True, True
        return False, False
    return False, False


def has_unfiltered_event(text: str, event: str) -> bool:
    return event_state(text, event) == (True, True)


def has_event(text: str, event: str) -> bool:
    return event_state(text, event)[0]


def has_workflow_run_source(text: str, source: str) -> bool:
    lines = _lines(text)
    for i, line in enumerate(lines):
        if not re.match(r"^\s*workflow_run\s*:\s*$", line):
            continue
        base = _indent(line)
        block = []
        for child in lines[i + 1:]:
            if child.strip() and _indent(child) <= base:
                break
            block.append(child)
        joined = "\n".join(block)
        workflow_ok = re.search(rf"workflows\s*:\s*\[[^\]]*[\"']{re.escape(source)}[\"'][^\]]*\]", joined)
        completed_ok = re.search(r"types\s*:\s*\[[^\]]*completed[^\]]*\]", joined)
        if workflow_ok and completed_ok:
            return True
    return False


def _jobs(text: str) -> dict[str, tuple[int, int, int]]:
    lines = _lines(text)
    ji = next((i for i, line in enumerate(lines) if re.match(r"^jobs\s*:\s*$", line)), None)
    if ji is None:
        return {}
    base = _indent(lines[ji])
    nested = []
    for i in range(ji + 1, len(lines)):
        if not lines[i].strip():
            continue
        if _indent(lines[i]) <= base:
            break
        nested.append(i)
    if not nested:
        return {}
    ind = min(_indent(lines[i]) for i in nested)
    starts = []
    for i in nested:
        if _indent(lines[i]) == ind:
            m = re.match(r"^([A-Za-z0-9_.-]+)\s*:\s*$", lines[i].strip())
            if m:
                starts.append((i, m.group(1)))
    return {job: (start, starts[n + 1][0] if n + 1 < len(starts) else len(lines), ind) for n, (start, job) in enumerate(starts)}


def _field(text: str, block: tuple[int, int, int], key: str) -> str | None:
    lines = _lines(text)
    start, end, ji = block
    children = [i for i in range(start + 1, end) if lines[i].strip() and _indent(lines[i]) > ji]
    if not children:
        return None
    fi = min(_indent(lines[i]) for i in children)
    for i in children:
        if _indent(lines[i]) == fi:
            m = re.match(rf"^{re.escape(key)}\s*:\s*(.*)$", lines[i].strip())
            if m:
                return m.group(1).strip()
    return None


def required_check_errors(relative: str, text: str, registry: dict[str, Any] | None = None) -> list[str]:
    registry = registry or load_required_check_registry()
    entries = [c for c in expanded_checks(registry) if c["workflow"] == relative]
    if not entries:
        return []
    errors = []
    jobs = _jobs(text)
    if any(c["kind"] == "workflow-job" and c["scope"] == "all-pr" for c in entries) and not has_unfiltered_event(text, "pull_request"):
        errors.append(f"{relative}: required workflow must use an unfiltered pull_request trigger")
    for item in entries:
        block = jobs.get(item["job"])
        if block is None:
            errors.append(f"{relative}: required check job '{item['job']}' is missing")
            continue
        if item["kind"] == "workflow-job":
            name = _field(text, block, "name")
            if name not in {item["context"], f'"{item["context"]}"', f"'{item['context']}'"}:
                errors.append(f"{relative}: required check job '{item['job']}' must keep name {item['context']!r}")
            if _field(text, block, "if") is not None:
                errors.append(f"{relative}: required check job '{item['job']}' must not use job-level if")
            if _field(text, block, "needs") is not None:
                errors.append(f"{relative}: required check job '{item['job']}' must not depend on another job")
        if _field(text, block, "continue-on-error") is not None:
            errors.append(f"{relative}: required check job '{item['job']}' must not use continue-on-error")
        raw = _field(text, block, "timeout-minutes")
        timeout = int(raw) if raw and raw.isdigit() else None
        if timeout != item["timeout_minutes"]:
            errors.append(f"{relative}: required check job '{item['job']}' timeout-minutes must remain {item['timeout_minutes']}")
    for item in entries:
        if item["kind"] != "commit-status":
            continue
        events = item["trigger"]["events"]
        if "workflow_run" in events and not has_workflow_run_source(text, item["trigger"]["source_workflow"]):
            errors.append(f"{relative}: trusted commit-status producer must run on completed workflow_run from {item['trigger']['source_workflow']!r}")
        if "workflow_dispatch" in events and not has_event(text, "workflow_dispatch"):
            errors.append(f"{relative}: trusted commit-status producer must retain workflow_dispatch recovery trigger")
    return errors


def repository_errors(root: Path = ROOT, registry_path: Path | None = None, policy_path: Path | None = None) -> list[str]:
    try:
        registry = load_required_check_registry(registry_path or root / "governance/required-checks.json", policy_path or root / "governance/policy.json")
    except RuntimeError as exc:
        return [str(exc)]
    errors = []
    for relative in sorted({c["workflow"] for c in expanded_checks(registry)}):
        path = root / relative
        if not path.is_file():
            errors.append(f"registered required-check workflow is missing: {relative}")
        else:
            errors.extend(required_check_errors(relative, path.read_text(encoding="utf-8"), registry))
    return errors


def live_expected(registry: dict[str, Any]) -> list[tuple[str, int | None]]:
    out = []
    seen = set()
    for item in expanded_checks(registry, {"all-pr"}):
        key = (item["context"], item["ruleset_integration_id"])
        if key not in seen:
            seen.add(key)
            out.append(key)
    return out


def live_ruleset_errors(registry: dict[str, Any], live: Any) -> list[str]:
    if not isinstance(live, dict):
        return ["live ruleset response must be an object"]
    errors = []
    contract = registry["ruleset"]
    if live.get("name") != contract["name"]:
        errors.append("live required-check ruleset name drift")
    if live.get("target") != "branch":
        errors.append("live required-check ruleset target must be branch")
    if live.get("enforcement") != "active":
        errors.append("live required-check ruleset must be active")
    ref = live.get("conditions", {}).get("ref_name", {}) if isinstance(live.get("conditions"), dict) else {}
    if "~DEFAULT_BRANCH" not in ref.get("include", []):
        errors.append("live required-check ruleset must include ~DEFAULT_BRANCH")
    rules = live.get("rules")
    if not isinstance(rules, list):
        return errors + ["live ruleset rules must be an array"]
    status_rules = [r for r in rules if isinstance(r, dict) and r.get("type") == "required_status_checks"]
    if len(status_rules) != 1:
        return errors + [f"live ruleset must define exactly one required_status_checks rule; found {len(status_rules)}"]
    params = status_rules[0].get("parameters")
    if not isinstance(params, dict):
        return errors + ["live required_status_checks parameters are missing"]
    if params.get("strict_required_status_checks_policy") is not True:
        errors.append("live strict_required_status_checks_policy must be true")
    raw = params.get("required_status_checks")
    if not isinstance(raw, list):
        return errors + ["live required_status_checks list is missing"]
    actual = []
    counts = {}
    for i, value in enumerate(raw):
        if not isinstance(value, dict) or not isinstance(value.get("context"), str) or not value["context"]:
            errors.append(f"live required status check at index {i} is malformed")
            continue
        context = value["context"]
        integration = value.get("integration_id")
        actual.append((context, integration))
        counts[context] = counts.get(context, 0) + 1
    for context, count in counts.items():
        if count > 1:
            errors.append(f"live ruleset contains duplicate required context {context!r}")
    expected = live_expected(registry)
    actual_set, expected_set = set(actual), set(expected)
    for context, integration in expected:
        if (context, integration) not in actual_set:
            same = [x[1] for x in actual if x[0] == context]
            errors.append(f"live required context {context!r} producer integration drift: expected {integration!r}, got {same!r}" if same else f"live ruleset is missing required context {context!r}")
    for context, integration in actual:
        if (context, integration) not in expected_set and not any(e[0] == context for e in expected):
            errors.append(f"live ruleset contains unknown required context {context!r} (integration_id={integration!r})")
    return errors


def _time(value: Any) -> dt.datetime | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return (parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.timezone.utc)).astimezone(dt.timezone.utc)


def _head(candidate: dict[str, Any], sha: str) -> bool:
    return candidate.get("head_sha", candidate.get("sha")) == sha


def _producer_error(item: dict[str, Any], candidate: dict[str, Any], sha: str, base_ref: str | None) -> str | None:
    if not _head(candidate, sha):
        return "non-current-head"
    if candidate.get("workflow") != item["workflow"]:
        return "workflow-producer-drift"
    producer = item["producer"]
    if item["kind"] == "workflow-job":
        if candidate.get("workflow_head_sha") not in {None, sha}:
            return "workflow-run-head-drift"
        app = candidate.get("app")
        if not isinstance(app, dict) or app.get("id") != producer["integration_id"] or app.get("slug") != producer["app_slug"]:
            return "check-app-drift"
        if candidate.get("workflow_event") != "pull_request":
            return "workflow-event-drift"
    else:
        creator = candidate.get("creator")
        if not isinstance(creator, dict) or creator.get("login") != producer["creator_login"]:
            return "status-creator-drift"
        if candidate.get("workflow_event") not in set(item["trigger"]["events"]):
            return "trusted-status-event-drift"
        if base_ref is not None and candidate.get("workflow_head_branch") != base_ref:
            return "trusted-status-ref-drift"
    return None


def _state(item: dict[str, Any], candidate: dict[str, Any]) -> str:
    if item["kind"] == "commit-status":
        return str(candidate.get("state") or "unknown")
    status = candidate.get("status")
    return str(status) if status in PENDING or status != "completed" else str(candidate.get("conclusion") or "unknown")


def _stamp(candidate: dict[str, Any]) -> dt.datetime:
    for key in ("completed_at", "updated_at", "started_at", "created_at"):
        parsed = _time(candidate.get(key))
        if parsed:
            return parsed
    return dt.datetime.min.replace(tzinfo=dt.timezone.utc)


def exact_entries(registry: dict[str, Any]) -> list[dict[str, Any]]:
    out = []
    seen = set()
    for value in expanded_checks(registry, {"all-pr"}):
        key = (value["kind"], value["workflow"], value["job"], value["context"])
        if key not in seen:
            seen.add(key)
            out.append(value)
    return out


def exact_head_report(registry: dict[str, Any], head_sha: str, check_runs: Iterable[dict[str, Any]], statuses: Iterable[dict[str, Any]], *, now: dt.datetime | None = None, trusted_base_ref: str | None = None) -> dict[str, Any]:
    current = (now or dt.datetime.now(dt.timezone.utc)).astimezone(dt.timezone.utc)
    runs = [x for x in check_runs if isinstance(x, dict)]
    status_values = [x for x in statuses if isinstance(x, dict)]
    gates = []
    overall = "pass"
    for item in exact_entries(registry):
        source = runs if item["kind"] == "workflow-job" else status_values
        key = "name" if item["kind"] == "workflow-job" else "context"
        candidates = [x for x in source if x.get(key) == item["context"]]
        current_candidates = [x for x in candidates if _head(x, head_sha)]
        valid = []
        producer_errors = []
        for candidate in current_candidates:
            error = _producer_error(item, candidate, head_sha, trusted_base_ref)
            valid.append(candidate) if error is None else producer_errors.append(error)
        gate = {
            "gate_id": item["gate_id"],
            "source_gate_id": item.get("source_gate_id", item["gate_id"]),
            "migration_role": item.get("migration_role", "current"),
            "context": item["context"],
            "kind": item["kind"],
            "workflow": item["workflow"],
            "decision": "fail",
            "reason": "missing-current-head",
        }
        if not valid:
            if current_candidates and producer_errors:
                gate.update(reason="producer-drift", producer_errors=sorted(set(producer_errors)))
            elif candidates:
                gate["reason"] = "previous-head-only"
            overall = "fail"
            gates.append(gate)
            continue
        selected = sorted(valid, key=lambda c: (_stamp(c), int(c.get("id", 0)) if str(c.get("id", "")).isdigit() else 0), reverse=True)[0]
        state = _state(item, selected)
        gate["observed_state"] = state
        if state == "success":
            gate.update(decision="pass", reason="accepted-success")
        elif state in PENDING:
            started = _time(selected.get("started_at")) or _time(selected.get("created_at"))
            timed_out = started is None or current - started > dt.timedelta(minutes=item["timeout_minutes"])
            if timed_out:
                gate["reason"] = "pending-timeout"
                overall = "fail"
            else:
                gate.update(decision="pending", reason="current-head-pending")
                if overall == "pass":
                    overall = "pending"
        else:
            gate["reason"] = "rejected-conclusion" if state in REJECTED else "unknown-state"
            overall = "fail"
        gates.append(gate)
    return {"schema_version": 1, "authoritative_head_sha": head_sha, "decision": overall, "gates": gates}


class GitHubApi:
    def __init__(self, token: str | None = None, api_url: str = "https://api.github.com"):
        self.token = token
        self.api_url = api_url.rstrip("/")

    def get(self, path: str) -> Any:
        url = path if path.startswith("https://") else f"{self.api_url}/{path.lstrip('/')}"
        headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "Coglatas-required-check-evaluator"}
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
                raw = response.read().decode()
        except (urllib.error.URLError, TimeoutError) as exc:
            raise RuntimeError(f"GitHub API request failed for {url}: {exc}") from exc
        try:
            return json.loads(raw)
        except json.JSONDecodeError as exc:
            raise RuntimeError(f"GitHub API returned malformed JSON for {url}: {exc}") from exc


def _run_id(url: Any) -> int | None:
    match = RUN_URL.search(url) if isinstance(url, str) else None
    return int(match.group("id")) if match else None


def _enrich(api: Any, repository: str, candidate: dict[str, Any], url_key: str, cache: dict[int, dict[str, Any]]) -> None:
    run_id = _run_id(candidate.get(url_key))
    if run_id is None:
        return
    if run_id not in cache:
        run = api.get(f"repos/{repository}/actions/runs/{run_id}")
        if not isinstance(run, dict):
            raise RuntimeError(f"GitHub Actions run {run_id} response is malformed")
        cache[run_id] = run
    run = cache[run_id]
    candidate.update(workflow=run.get("path"), workflow_event=run.get("event"), workflow_head_sha=run.get("head_sha"), workflow_head_branch=run.get("head_branch"))


def _ruleset(api: Any, repository: str, registry: dict[str, Any]) -> dict[str, Any]:
    all_rulesets = api.get(f"repos/{repository}/rulesets")
    if not isinstance(all_rulesets, list):
        raise RuntimeError("GitHub ruleset list response must be an array")
    found = [r for r in all_rulesets if isinstance(r, dict) and r.get("name") == registry["ruleset"]["name"]]
    if len(found) != 1 or not isinstance(found[0].get("id"), int):
        raise RuntimeError("expected exactly one registered live ruleset")
    live = api.get(f"repos/{repository}/rulesets/{found[0]['id']}")
    if not isinstance(live, dict):
        raise RuntimeError("GitHub ruleset detail response must be an object")
    return live


def _pages(api: Any, path: str, key: str | None) -> list[dict[str, Any]]:
    out = []
    page = 1
    while True:
        payload = api.get(path if page == 1 else f"{path}&page={page}")
        batch = payload.get(key) if key and isinstance(payload, dict) else payload
        if not isinstance(batch, list):
            raise RuntimeError("GitHub paginated response is malformed")
        values = [dict(x) for x in batch if isinstance(x, dict)]
        out.extend(values)
        total = payload.get("total_count") if isinstance(payload, dict) else None
        if len(values) < 100 or (isinstance(total, int) and len(out) >= total):
            return out
        page += 1
        if page > 100:
            raise RuntimeError("GitHub pagination exceeded safety bound")


def evaluate_live_pr(api: Any, repository: str, pr_number: int, registry: dict[str, Any], *, now: dt.datetime | None = None) -> dict[str, Any]:
    pr = api.get(f"repos/{repository}/pulls/{pr_number}")
    if not isinstance(pr, dict) or pr.get("state") != "open":
        raise RuntimeError(f"PR #{pr_number} is missing or not open")
    sha = pr.get("head", {}).get("sha") if isinstance(pr.get("head"), dict) else None
    base = pr.get("base", {}).get("ref") if isinstance(pr.get("base"), dict) else None
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", sha) or not isinstance(base, str) or not base:
        raise RuntimeError("Pull Request API response is missing authoritative head/base")
    ruleset_errors = live_ruleset_errors(registry, _ruleset(api, repository, registry))
    checks = _pages(api, f"repos/{repository}/commits/{sha}/check-runs?filter=latest&per_page=100", "check_runs")
    statuses = _pages(api, f"repos/{repository}/commits/{sha}/statuses?per_page=100", None)
    for candidate in checks:
        candidate.setdefault("head_sha", sha)
    for candidate in statuses:
        candidate.setdefault("sha", sha)
    contexts = {x["context"] for x in exact_entries(registry)}
    cache = {}
    for candidate in checks:
        if candidate.get("name") in contexts:
            _enrich(api, repository, candidate, "details_url", cache)
    for candidate in statuses:
        if candidate.get("context") in contexts:
            _enrich(api, repository, candidate, "target_url", cache)
    exact = exact_head_report(registry, sha, checks, statuses, now=now, trusted_base_ref=base)
    decision = "fail" if ruleset_errors else exact["decision"]
    return {
        "schema_version": 1,
        "repository": repository,
        "pr_number": pr_number,
        "authoritative_head_sha": sha,
        "authoritative_base_ref": base,
        "decision": decision,
        "live_ruleset": {"decision": "pass" if not ruleset_errors else "fail", "errors": ruleset_errors},
        "exact_head": exact,
    }


def run_cli(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--live-pr", type=int)
    parser.add_argument("--repository")
    parser.add_argument("--policy", type=Path, default=POLICY_PATH)
    parser.add_argument("--registry", type=Path, default=REGISTRY_PATH)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    if args.live_pr is None:
        errors = repository_errors(registry_path=args.registry, policy_path=args.policy)
        if errors:
            print("Required PR check policy failed:", file=os.sys.stderr)
            for error in sorted(set(errors)):
                print(f"- {error}", file=os.sys.stderr)
            return 1
        print("Required PR check policy passed.")
        return 0
    repository = args.repository or os.environ.get("GITHUB_REPOSITORY")
    try:
        registry = load_required_check_registry(args.registry, args.policy)
        if not isinstance(repository, str) or not re.fullmatch(r"[^/\s]+/[^/\s]+", repository):
            raise RuntimeError("live repository must be owner/name")
        report = evaluate_live_pr(
            GitHubApi(os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN"), os.environ.get("GITHUB_API_URL", "https://api.github.com")),
            repository,
            args.live_pr,
            registry,
        )
    except RuntimeError as exc:
        report = {"schema_version": 1, "repository": repository, "pr_number": args.live_pr, "decision": "fail", "error": str(exc)}
    print(json.dumps(report, ensure_ascii=False, sort_keys=True) if args.json else f"Required-check live evaluation: decision={report['decision']} pr={args.live_pr} head={report.get('authoritative_head_sha', 'unknown')}")
    return 0 if report["decision"] == "pass" else (2 if report["decision"] == "pending" else 1)
