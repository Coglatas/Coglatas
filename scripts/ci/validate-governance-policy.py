#!/usr/bin/env python3
"""Strict validator for the repository-owned Governance CI policy contract."""

from __future__ import annotations

import fnmatch
import json
import re
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
REQUIRED_FAMILIES = {
    "ruleset", "required-status-checks", "required-signatures",
    "pr-review-codeowners", "bypass-actors", "workflow-permissions",
    "workflow-trust-boundary", "self-hosted-runner", "action-refs",
    "governance-sensitive-paths", "waiver-expiry", "governance-evidence",
}
CRITICAL_SENSITIVE_PATHS = (
    ".github/CODEOWNERS",
    ".github/workflows/ci.yml",
    ".github/workflows/publication-readiness.yml",
    "scripts/ci/check-required-pr-checks.py",
    "scripts/ci/validate-governance-policy.py",
    "governance/policy.json",
    "governance/policy.schema.json",
)
FORBIDDEN_CATCH_ALL_GLOBS = {"*", "**", "**/*", "./**", "./**/*", "/**", "/**/*"}


def _load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _kind(value: Any, name: str) -> bool:
    return {
        "object": isinstance(value, dict),
        "array": isinstance(value, list),
        "string": isinstance(value, str),
        "integer": isinstance(value, int) and not isinstance(value, bool),
        "number": isinstance(value, (int, float)) and not isinstance(value, bool),
        "boolean": isinstance(value, bool),
        "null": value is None,
    }.get(name, False)


def _resolve(schema: dict[str, Any], ref: str) -> Any:
    if not ref.startswith("#/"):
        raise ValueError(f"unsupported schema reference: {ref}")
    node: Any = schema
    for part in ref[2:].split("/"):
        key = part.replace("~1", "/").replace("~0", "~")
        if not isinstance(node, dict) or key not in node:
            raise ValueError(f"unresolvable schema reference: {ref}")
        node = node[key]
    return node


def _marker(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _schema_errors(value: Any, rule: Any, root: dict[str, Any], path: str = "$") -> list[str]:
    if not isinstance(rule, dict):
        return [f"{path}: schema node must be an object"]
    if "$ref" in rule:
        try:
            return _schema_errors(value, _resolve(root, rule["$ref"]), root, path)
        except ValueError as exc:
            return [f"{path}: {exc}"]

    errors: list[str] = []
    expected_type = rule.get("type")
    if expected_type is not None and (
        not isinstance(expected_type, str) or not _kind(value, expected_type)
    ):
        return [f"{path}: expected type {expected_type!r}"]

    if "const" in rule and value != rule["const"]:
        errors.append(f"{path}: expected constant {_marker(rule['const'])}")
    if "enum" in rule and value not in rule["enum"]:
        errors.append(f"{path}: value {_marker(value)} is not in enum")

    if isinstance(value, str):
        if isinstance(rule.get("minLength"), int) and len(value) < rule["minLength"]:
            errors.append(f"{path}: string is shorter than minLength {rule['minLength']}")
        if isinstance(rule.get("pattern"), str) and not re.search(rule["pattern"], value):
            errors.append(f"{path}: string does not match pattern {rule['pattern']!r}")

    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if isinstance(rule.get("minimum"), (int, float)) and value < rule["minimum"]:
            errors.append(f"{path}: value is below minimum {rule['minimum']}")
        if isinstance(rule.get("maximum"), (int, float)) and value > rule["maximum"]:
            errors.append(f"{path}: value is above maximum {rule['maximum']}")

    if isinstance(value, list):
        if isinstance(rule.get("minItems"), int) and len(value) < rule["minItems"]:
            errors.append(f"{path}: array has fewer than {rule['minItems']} items")
        if rule.get("uniqueItems") is True:
            seen: set[str] = set()
            for index, item in enumerate(value):
                token = _marker(item)
                if token in seen:
                    errors.append(f"{path}[{index}]: duplicate array item")
                seen.add(token)
        if isinstance(rule.get("items"), dict):
            for index, item in enumerate(value):
                errors += _schema_errors(item, rule["items"], root, f"{path}[{index}]")

    if isinstance(value, dict):
        if isinstance(rule.get("minProperties"), int) and len(value) < rule["minProperties"]:
            errors.append(f"{path}: object has fewer than {rule['minProperties']} properties")
        for key in rule.get("required", []):
            if key not in value:
                errors.append(f"{path}: required field {key!r} is missing")
        props = rule.get("properties", {})
        props = props if isinstance(props, dict) else {}
        for key, child in props.items():
            if key in value and isinstance(child, dict):
                errors += _schema_errors(value[key], child, root, f"{path}.{key}")
        extras = [key for key in value if key not in props]
        additional = rule.get("additionalProperties", True)
        if additional is False:
            errors += [f"{path}: unknown field {key!r}" for key in extras]
        elif isinstance(additional, dict):
            for key in extras:
                errors += _schema_errors(value[key], additional, root, f"{path}.{key}")

    for child in rule.get("allOf", []):
        if isinstance(child, dict):
            errors += _schema_errors(value, child, root, path)
    condition = rule.get("if")
    if isinstance(condition, dict) and not _schema_errors(value, condition, root, path):
        then = rule.get("then")
        if isinstance(then, dict):
            errors += _schema_errors(value, then, root, path)
    return errors


def validate_against_schema(policy: Any, schema: dict[str, Any]) -> list[str]:
    return _schema_errors(policy, schema, schema)


def _safe_path(root: Path, relative: str) -> tuple[Path | None, str | None]:
    if not relative or relative.startswith(("/", "\\")) or ".." in Path(relative).parts:
        return None, "must be a repository-relative path without '..'"
    path = (root / relative).resolve()
    try:
        path.relative_to(root.resolve())
    except ValueError:
        return None, "resolves outside the repository"
    return path, None


def _strip_comment(line: str) -> str:
    quote: str | None = None
    escaped = False
    out: list[str] = []
    for char in line:
        if escaped:
            out.append(char)
            escaped = False
        elif char == "\\" and quote == '"':
            out.append(char)
            escaped = True
        elif char in {"'", '"'}:
            quote = None if quote == char else (char if quote is None else quote)
            out.append(char)
        elif char == "#" and quote is None:
            break
        else:
            out.append(char)
    return "".join(out).rstrip()


def workflow_job_ids(text: str) -> set[str]:
    lines = [_strip_comment(line) for line in text.splitlines()]
    jobs = next((i for i, line in enumerate(lines) if re.match(r"^jobs\s*:\s*$", line)), None)
    if jobs is None:
        return set()
    base = len(lines[jobs]) - len(lines[jobs].lstrip())
    children = [
        i for i in range(jobs + 1, len(lines))
        if lines[i].strip() and len(lines[i]) - len(lines[i].lstrip()) > base
    ]
    if not children:
        return set()
    indent = min(len(lines[i]) - len(lines[i].lstrip()) for i in children)
    result: set[str] = set()
    for i in children:
        if len(lines[i]) - len(lines[i].lstrip()) != indent:
            continue
        match = re.match(r"^([A-Za-z0-9_.-]+)\s*:\s*$", lines[i].strip())
        if match:
            result.add(match.group(1))
    return result


def _repo_files(root: Path) -> list[str]:
    return [
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_file() and ".git" not in path.parts
    ]


def _validate_sensitive(control: dict[str, Any], root: Path) -> list[str]:
    errors: list[str] = []
    expected = control["expected"]
    patterns = expected["patterns"]
    files = _repo_files(root)
    for pattern in patterns:
        value = pattern.strip()
        if value in FORBIDDEN_CATCH_ALL_GLOBS:
            errors.append(f"{control['id']}: sensitive path pattern {pattern!r} is an over-broad catch-all")
            continue
        if value.startswith(("!", "/")) or ".." in Path(value).parts:
            errors.append(f"{control['id']}: sensitive path pattern {pattern!r} is unsafe")
            continue
        if expected["require_nonempty_match"] and not any(fnmatch.fnmatchcase(f, value) for f in files):
            errors.append(f"{control['id']}: sensitive path pattern {pattern!r} matches no repository files")
    for critical in CRITICAL_SENSITIVE_PATHS:
        if not any(fnmatch.fnmatchcase(critical, pattern) for pattern in patterns):
            errors.append(f"{control['id']}: critical governance path {critical!r} is not classified as sensitive")
    return errors


def _validate_references(policy: dict[str, Any], root: Path) -> list[str]:
    errors: list[str] = []
    jobs: dict[str, set[str]] = {}
    for control in policy["controls"]:
        cid = control["id"]
        refs = control["references"]
        for kind in ("paths", "workflows"):
            for relative in refs[kind]:
                path, problem = _safe_path(root, relative)
                if problem or path is None:
                    errors.append(f"{cid}: {kind[:-1]} {relative!r} {problem}")
                    continue
                if kind == "workflows" and (
                    not relative.startswith(".github/workflows/")
                    or path.suffix not in {".yml", ".yaml"}
                ):
                    errors.append(f"{cid}: workflow reference {relative!r} must point under .github/workflows/")
                if not path.is_file():
                    errors.append(f"{cid}: referenced {kind[:-1]} {relative!r} does not exist")
        for check in refs["checks"]:
            workflow, job = check["workflow"], check["job"]
            if workflow not in refs["workflows"]:
                errors.append(f"{cid}: required check workflow {workflow!r} is not listed in references.workflows")
            path, problem = _safe_path(root, workflow)
            if problem or path is None:
                errors.append(f"{cid}: required check workflow {workflow!r} {problem}")
                continue
            if not path.is_file():
                errors.append(f"{cid}: required check workflow {workflow!r} does not exist")
                continue
            jobs.setdefault(workflow, workflow_job_ids(path.read_text(encoding="utf-8")))
            if job not in jobs[workflow]:
                errors.append(f"{cid}: required check job {job!r} does not exist in {workflow}")
    return errors


def validate_policy_semantics(policy: dict[str, Any], root: Path | None = None) -> list[str]:
    controls = policy.get("controls")
    if not isinstance(controls, list):
        return ["$.controls: must be an array before semantic validation"]
    errors: list[str] = []
    seen: set[str] = set()
    families: set[str] = set()
    waivers = policy.get("waiver_rules") if isinstance(policy.get("waiver_rules"), dict) else {}

    for index, control in enumerate(controls):
        if not isinstance(control, dict):
            continue
        cid = control.get("id")
        if isinstance(cid, str):
            if cid in seen:
                errors.append(f"{cid}: duplicate control ID")
            seen.add(cid)
        family = control.get("family")
        if isinstance(family, str):
            families.add(family)
        if control.get("enforcement") == "blocking":
            if not control.get("owner"):
                errors.append(f"{cid or index}: blocking control owner is missing")
            evidence = control.get("evidence")
            if not isinstance(evidence, dict) or not evidence.get("kind") or not evidence.get("source"):
                errors.append(f"{cid or index}: blocking control evidence is missing")
        exception = control.get("exception")
        if isinstance(exception, dict):
            if exception.get("allowed") is True:
                rule = exception.get("waiver_rule")
                if not isinstance(rule, str) or not rule:
                    errors.append(f"{cid or index}: exceptionable control must name a waiver rule")
                elif rule not in waivers:
                    errors.append(f"{cid or index}: waiver rule {rule!r} does not exist")
            elif "waiver_rule" in exception:
                errors.append(f"{cid or index}: non-exceptionable control must not name a waiver rule")
        expected = control.get("expected", {})
        refs = control.get("references", {})
        if family == "required-status-checks" and expected.get("required") != refs.get("checks"):
            errors.append(f"{cid}: expected.required and references.checks must be identical")
        if family == "waiver-expiry" and expected.get("rule_name") not in waivers:
            errors.append(f"{cid}: expected waiver rule {expected.get('rule_name')!r} does not exist")
        if family == "governance-sensitive-paths" and root is not None:
            try:
                errors += _validate_sensitive(control, root)
            except (KeyError, TypeError):
                pass

    for family in sorted(REQUIRED_FAMILIES - families):
        errors.append(f"policy: required control family {family!r} is missing")
    semantics = policy.get("semantics")
    if isinstance(semantics, dict) and semantics.get("live_state_is_baseline") is not False:
        errors.append("policy: live GitHub state must never be adopted as the baseline")
    if root is not None:
        errors += _validate_references(policy, root)
    return errors


def render_controls_markdown(policy: dict[str, Any]) -> str:
    lines = [
        "# Governance control matrix", "",
        "> Generated view. The source of truth is `governance/policy.json`; do not edit this matrix as policy.",
        "> `scripts/ci/validate-governance-policy.py` fails if this file drifts from the machine-readable contract.",
        "", "## Policy semantics", "",
        f"- Policy ID: `{policy['policy_id']}` v{policy['version']}",
        f"- Repository: `{policy['repository']}`",
        f"- Repository ID: `{policy['repository_id']}`",
        "- Transfer aliases: " + ", ".join(f"`{item}`" for item in policy["repository_aliases"]),
        f"- Default branch: `{policy['default_branch']}`",
        "- Live GitHub state is evidence, **not** the baseline.",
        "- Unknown controls/fields are rejected; invalid policy blocks.",
        "- Critical controls have no implicit permissive defaults.",
        "- Enforcement downgrades must appear as explicit reviewed policy diffs.",
        "", "## Control matrix", "",
        "| ID | Family | Scope | Enforcement | Owner | Evidence | Exception | Title |",
        "| --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    for control in policy["controls"]:
        exception = control["exception"]
        exception_text = f"allowed via `{exception['waiver_rule']}`" if exception["allowed"] else "forbidden"
        lines.append("| " + " | ".join([
            f"`{control['id']}`", f"`{control['family']}`",
            ", ".join(f"`{scope}`" for scope in control["scope"]),
            f"`{control['enforcement']}`", f"`{control['owner']}`",
            f"`{control['evidence']['kind']}`", exception_text,
            control["title"].replace("|", r"\|"),
        ]) + " |")
    lines += ["", "## Expected values", ""]
    for control in policy["controls"]:
        lines += [
            f"### `{control['id']}` — {control['title']}", "",
            control["purpose"], "", "```json",
            json.dumps(control["expected"], indent=2, ensure_ascii=False, sort_keys=True),
            "```", "",
        ]
    lines += [
        "## Consumer contract", "",
        "GOV-02 through GOV-10 must consume this policy contract rather than learning an expected baseline from current GitHub settings. Live settings, workflow inventories, review state, waivers, and evidence are inputs to compare against the expected values above.",
        "",
    ]
    return "\n".join(lines)


def repository_errors(root: Path = ROOT) -> list[str]:
    policy_path = root / "governance/policy.json"
    schema_path = root / "governance/policy.schema.json"
    docs_path = root / "governance/controls.md"
    missing = [
        f"{path.relative_to(root)}: required governance contract file is missing"
        for path in (policy_path, schema_path) if not path.is_file()
    ]
    if missing:
        return missing
    try:
        policy = _load_json(policy_path)
    except (OSError, json.JSONDecodeError) as exc:
        return [f"governance/policy.json: invalid JSON: {exc}"]
    try:
        schema = _load_json(schema_path)
    except (OSError, json.JSONDecodeError) as exc:
        return [f"governance/policy.schema.json: invalid JSON: {exc}"]
    if not isinstance(schema, dict):
        return ["governance/policy.schema.json: schema root must be an object"]
    errors = validate_against_schema(policy, schema)
    if errors:
        return errors
    errors += validate_policy_semantics(policy, root)
    expected_docs = render_controls_markdown(policy)
    if not docs_path.is_file():
        errors.append("governance/controls.md: generated control matrix is missing")
    elif docs_path.read_text(encoding="utf-8") != expected_docs:
        errors.append("governance/controls.md: generated control matrix is stale; regenerate it from governance/policy.json")
    return errors


def main() -> int:
    errors = sorted(set(repository_errors()))
    if errors:
        print("Governance policy validation failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1
    policy = _load_json(ROOT / "governance/policy.json")
    print(
        "Governance policy validation passed: "
        f"{len(policy['controls'])} controls, {len(REQUIRED_FAMILIES)} required families, "
        "strict schema + references verified."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
