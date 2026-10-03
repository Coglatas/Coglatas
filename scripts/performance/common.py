#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import os
import tempfile
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

FIXTURE_VERSION = 1
DB_FIXTURE_VERSION = 2
SAFE_TARGET_HOSTS = frozenset({"127.0.0.1", "localhost", "::1", "performance-app", "coglatas-performance"})
REQUIRED_COUNTS = frozenset({
    "tenants", "workspaces", "projects", "tasks", "workItems", "milestones",
    "dependencies", "members", "messages", "notifications", "announcements", "files",
})
REQUIRED_FOCUS = frozenset({
    "workspaceProjects", "projectTasks", "projectMilestones", "projectDependencies",
    "userMyTasks", "kanbanAuthorizedCards", "workspaceFiles", "conversations",
    "conversationMessages", "userNotifications", "visibleAnnouncements",
})


class PerformanceContractError(RuntimeError):
    pass


def repository_root() -> Path:
    return Path(__file__).resolve().parents[2]


def load_json(path: Path) -> dict[str, Any]:
    try:
        # Runtime evidence may be emitted by .NET with a UTF-8 BOM. `utf-8-sig`
        # accepts that canonical UTF-8 form while remaining strict about invalid
        # byte sequences and malformed JSON.
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise PerformanceContractError(f"cannot read JSON {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise PerformanceContractError(f"{path} must contain a JSON object")
    return value


def load_profile(profile_name: str, manifest_path: Path | None = None) -> tuple[dict[str, Any], dict[str, Any]]:
    path = manifest_path or repository_root() / "performance" / "datasets.json"
    document = load_json(path)
    if document.get("schemaVersion") != 1 or document.get("seedManifestVersion") != 1:
        raise PerformanceContractError("unsupported performance dataset manifest version")

    profiles = document.get("profiles")
    if not isinstance(profiles, dict) or profile_name not in profiles:
        allowed = ", ".join(sorted(profiles or {}))
        raise PerformanceContractError(f"unknown performance profile {profile_name!r}; expected one of: {allowed}")

    profile = profiles[profile_name]
    if not isinstance(profile, dict):
        raise PerformanceContractError(f"profile {profile_name!r} must be an object")
    if not isinstance(profile.get("seed"), int) or profile["seed"] <= 0:
        raise PerformanceContractError(f"profile {profile_name!r} requires a positive integer seed")
    counts = profile.get("counts")
    focus = profile.get("focus")
    if not isinstance(counts, dict) or REQUIRED_COUNTS - counts.keys():
        missing = sorted(REQUIRED_COUNTS - (counts.keys() if isinstance(counts, dict) else set()))
        raise PerformanceContractError(f"profile {profile_name!r} missing counts: {missing}")
    if not isinstance(focus, dict) or REQUIRED_FOCUS - focus.keys():
        missing = sorted(REQUIRED_FOCUS - (focus.keys() if isinstance(focus, dict) else set()))
        raise PerformanceContractError(f"profile {profile_name!r} missing focus values: {missing}")
    if any(not isinstance(value, int) or value < 0 for value in counts.values()):
        raise PerformanceContractError(f"profile {profile_name!r} counts must be non-negative integers")
    if any(not isinstance(value, int) or value < 0 for value in focus.values()):
        raise PerformanceContractError(f"profile {profile_name!r} focus values must be non-negative integers")
    return document, profile


def active_fixture_version() -> int:
    return DB_FIXTURE_VERSION if os.environ.get("COGLATAS_PERFORMANCE_DB_CAPTURE_ENABLED", "false").lower() == "true" else FIXTURE_VERSION


def fixture_hash(profile_name: str, manifest_path: Path | None = None, *, fixture_version: int | None = None) -> str:
    path = manifest_path or repository_root() / "performance" / "datasets.json"
    _, profile = load_profile(profile_name, path)
    manifest_sha256 = hashlib.sha256(path.read_bytes()).hexdigest()
    canonical = (
        f"fixtureVersion={active_fixture_version() if fixture_version is None else fixture_version}\n"
        f"manifestSha256={manifest_sha256}\n"
        f"profile={profile_name}\n"
        f"seed={profile['seed']}\n"
    ).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def validate_target(base_url: str) -> str:
    parsed = urlsplit(base_url)
    if parsed.scheme != "http":
        raise PerformanceContractError("performance target must use plain HTTP inside the isolated local/Compose boundary")
    if parsed.username or parsed.password:
        raise PerformanceContractError("performance target must not contain URL credentials")
    hostname = (parsed.hostname or "").lower()
    if hostname not in SAFE_TARGET_HOSTS:
        raise PerformanceContractError(
            f"performance target host {hostname!r} is not isolated; allowed hosts: {sorted(SAFE_TARGET_HOSTS)}"
        )
    if parsed.path not in ("", "/") or parsed.query or parsed.fragment:
        raise PerformanceContractError("performance base URL must not include a path, query, or fragment")
    if parsed.port is None:
        raise PerformanceContractError("performance target must use an explicit port")
    return base_url.rstrip("/")


def validate_fixture_evidence(
    evidence: dict[str, Any],
    profile_name: str,
    manifest_path: Path | None = None,
) -> dict[str, Any]:
    document, profile = load_profile(profile_name, manifest_path)
    expected_hash = fixture_hash(profile_name, manifest_path)
    required = {
        "schemaVersion": 1,
        "fixtureVersion": active_fixture_version(),
        "seedManifestVersion": document["seedManifestVersion"],
        "profile": profile_name,
        "seed": profile["seed"],
        "fixtureHash": expected_hash,
        "migrationStatus": "current",
        "complete": True,
    }
    for key, expected in required.items():
        if evidence.get(key) != expected:
            raise PerformanceContractError(
                f"fixture evidence {key} mismatch: expected {expected!r}, got {evidence.get(key)!r}"
            )
    cardinalities = evidence.get("cardinalities")
    if cardinalities != profile["counts"]:
        raise PerformanceContractError("fixture cardinalities do not match PERF-01 counts")
    focus = evidence.get("focus")
    if focus != profile["focus"]:
        raise PerformanceContractError("fixture focus cardinalities do not match PERF-01 focus")
    identities = evidence.get("identities")
    if not isinstance(identities, dict):
        raise PerformanceContractError("fixture evidence must contain stable identities")
    for key in ("tenantSlug", "operatorEmail", "workspaceId", "taskListProjectId", "ganttProjectId", "kanbanProjectId"):
        value = identities.get(key)
        if not isinstance(value, str) or not value.strip():
            raise PerformanceContractError(f"fixture evidence missing stable identity {key}")
    return evidence


def write_json_atomic(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=path.parent, delete=False) as handle:
        json.dump(value, handle, ensure_ascii=False, sort_keys=True, indent=2)
        handle.write("\n")
        temporary = Path(handle.name)
    os.replace(temporary, path)
