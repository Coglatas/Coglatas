#!/usr/bin/env python3
"""Require green MVP-A checks from trusted GitHub Actions for the exact candidate commit."""

from __future__ import annotations

import json
import io
import hashlib
import os
import re
import sys
import zipfile
import importlib.util
from pathlib import Path
import urllib.error
import urllib.request
import urllib.parse

GITHUB_ACTIONS_APP_ID = 15368
GITHUB_ACTIONS_APP_SLUG = "github-actions"
PAGE_SIZE = 100
MAX_PAGES = 20
REQUIRED_CHECKS = (
    "Main Test / Frontend / Security / Main Test",
    "Main Test / Frontend / Security / Main Frontend",
    "Main Test / Frontend / Security / Main Security",
    "publication-readiness",
    "frontend-static-analysis",
    "Real-backend E2E from main artifacts / licensed-real-backend",
    "functional-full",
    "sbom-source",
    "SBOM image scan from main artifacts / sbom-image-trusted",
)

EVIDENCE_SPEC = importlib.util.spec_from_file_location(
    "functional_evidence", Path(__file__).with_name("functional_evidence.py")
)
assert EVIDENCE_SPEC and EVIDENCE_SPEC.loader
functional_evidence = importlib.util.module_from_spec(EVIDENCE_SPEC)
EVIDENCE_SPEC.loader.exec_module(functional_evidence)


def required_env(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise RuntimeError(f"{name} is required for MVP-A final check verification.")
    return value


def fetch_page(url: str, token: str, sha: str) -> dict[str, object]:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "User-Agent": "coglatas-mvp-a-final-gate",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(
            f"Unable to read check runs for {sha}: HTTP {error.code}."
        ) from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"Unable to read check runs for {sha}: request failed.") from error

    if not isinstance(payload, dict):
        raise RuntimeError(f"Unable to read check runs for {sha}: GitHub returned a non-object payload.")
    return payload


def fetch_check_runs(repository: str, sha: str, token: str, api_url: str) -> list[dict[str, object]]:
    check_runs: list[dict[str, object]] = []

    for page in range(1, MAX_PAGES + 1):
        url = (
            f"{api_url}/repos/{repository}/commits/{sha}/check-runs"
            f"?per_page={PAGE_SIZE}&page={page}&filter=all"
        )
        payload = fetch_page(url, token, sha)
        raw_items = payload.get("check_runs", [])
        if not isinstance(raw_items, list):
            raise RuntimeError(f"Unable to read check runs for {sha}: 'check_runs' is not a list.")

        items = [item for item in raw_items if isinstance(item, dict)]
        check_runs.extend(items)

        total_count = payload.get("total_count")
        if isinstance(total_count, int) and len(check_runs) >= total_count:
            return check_runs
        if len(raw_items) < PAGE_SIZE:
            return check_runs

    raise RuntimeError(
        f"Unable to read all check runs for {sha}: pagination exceeded {MAX_PAGES} pages."
    )


def check_run_id(check: dict[str, object]) -> int | None:
    """Extract and validate the check run ID from a check run object.

    Args:
        check: A GitHub check run dict

    Returns:
        The check run ID as a positive integer, or None if invalid or missing
    """
    raw_id = check.get("id")
    if isinstance(raw_id, bool) or not isinstance(raw_id, int) or raw_id <= 0:
        return None
    return raw_id


def check_app_identity(check: dict[str, object]) -> tuple[int | None, str]:
    app = check.get("app")
    if not isinstance(app, dict):
        return None, ""

    raw_id = app.get("id")
    app_id = raw_id if isinstance(raw_id, int) else None
    raw_slug = app.get("slug")
    slug = raw_slug if isinstance(raw_slug, str) else ""
    return app_id, slug


def is_trusted_required_check(check: dict[str, object], sha: str) -> bool:
    app_id, slug = check_app_identity(check)
    return (
        check.get("head_sha") == sha
        and app_id == GITHUB_ACTIONS_APP_ID
        and slug == GITHUB_ACTIONS_APP_SLUG
    )


def is_green_required_check(check: dict[str, object]) -> bool:
    """Reduce live check-run state to a bounded boolean before any log output."""
    return check.get("status") == "completed" and check.get("conclusion") == "success"


def evaluate_required_checks(
    check_runs: list[dict[str, object]], sha: str
) -> tuple[list[str], list[tuple[str, bool]]]:
    """Evaluate required checks by selecting the latest trusted check run by ID.

    For each required check, finds all trusted check runs for the exact SHA,
    validates their IDs, and selects the one with the highest ID as latest.
    Uses check run ID ordering instead of timestamps to avoid masking attacks.

    Args:
        check_runs: List of GitHub check run dicts to evaluate
        sha: The exact commit SHA to match against

    Returns:
        Tuple of (failure messages, summary of check results as name-boolean pairs)
    """
    failures: list[str] = []
    summary: list[tuple[str, bool]] = []

    for name in REQUIRED_CHECKS:
        named_matches = [check for check in check_runs if check.get("name") == name]
        trusted_matches = [
            check for check in named_matches if is_trusted_required_check(check, sha)
        ]

        if not trusted_matches:
            failures.append(f"{name}: no trusted GitHub Actions check run exists for exact candidate")
            summary.append((name, False))
            continue

        invalid_ids = [check for check in trusted_matches if check_run_id(check) is None]
        if invalid_ids:
            failures.append(f"{name}: trusted check run has an invalid or missing id")
            summary.append((name, False))
            continue

        latest = max(trusted_matches, key=lambda check: check_run_id(check) or 0)
        green = is_green_required_check(latest)
        summary.append((name, green))
        if not green:
            failures.append(f"{name}: latest trusted check is not completed successfully")

    return failures, summary


def read_manifest_archive(archive: bytes) -> dict:
    """Read a bounded metadata document without extracting artifact paths."""
    if len(archive) > 1024 * 1024:
        raise RuntimeError("Functional evidence archive exceeds its metadata budget.")
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
            entries = zipped.infolist()
            if len(entries) != 1 or entries[0].filename != "manifest.json" or entries[0].file_size > 128 * 1024:
                raise RuntimeError("Functional evidence archive has unexpected contents.")
            return json.loads(zipped.read(entries[0]))
    except (zipfile.BadZipFile, json.JSONDecodeError) as error:
        raise RuntimeError("Functional evidence artifact is invalid.") from error


class MetadataRedirectHandler(urllib.request.HTTPRedirectHandler):
    """A signed artifact redirect must never receive the GitHub API token."""
    def redirect_request(self, request, response, code, message, headers, new_url):
        if urllib.parse.urlsplit(new_url).scheme != "https":
            raise RuntimeError("Functional artifact redirect must use HTTPS.")
        redirected = super().redirect_request(request, response, code, message, headers, new_url)
        if redirected is not None:
            redirected.remove_header("Authorization")
        return redirected


def verify_functional_evidence(check_runs: list[dict[str, object]], repository: str, sha: str, token: str, api_url: str) -> dict:
    checks = [check for check in check_runs if check.get("name") == "functional-full" and is_trusted_required_check(check, sha)]
    if not checks or any(check_run_id(check) is None for check in checks):
        raise RuntimeError("No valid exact-candidate Functional aggregate exists.")
    latest = max(checks, key=lambda check: check_run_id(check) or 0)
    if not is_green_required_check(latest):
        raise RuntimeError("Latest Functional aggregate is not successful.")
    details_url = latest.get("details_url")
    match = re.fullmatch(r"https://github\.com/" + re.escape(repository) + r"/actions/runs/(\d+)/job/\d+(?:\?.*)?", str(details_url))
    if not match:
        raise RuntimeError("Functional check has no trusted workflow-run reference.")
    run_id = match.group(1)
    base = f"{api_url}/repos/{repository}"
    run = fetch_page(f"{base}/actions/runs/{run_id}", token, sha)
    if run.get("head_sha") != sha or run.get("event") != "push" or run.get("head_branch") != "main" or run.get("path") != ".github/workflows/main-build-artifacts.yml" or run.get("status") != "completed" or run.get("conclusion") == "cancelled":
        raise RuntimeError("Functional evidence was not produced by the exact trusted main run.")
    attempt = run.get("run_attempt")
    if type(attempt) is not int or attempt < 1:
        raise RuntimeError("Functional workflow attempt is invalid.")
    artifact_name = f"functional-evidence-functional-full-{sha}-{attempt}"
    payload = fetch_page(f"{base}/actions/runs/{run_id}/artifacts?per_page=100", token, sha)
    artifacts = payload.get("artifacts")
    if not isinstance(artifacts, list) or payload.get("total_count", 0) > 100:
        raise RuntimeError("Unable to obtain bounded Functional artifact inventory.")
    matches = [artifact for artifact in artifacts if isinstance(artifact, dict) and artifact.get("name") == artifact_name and artifact.get("expired") is False]
    if len(matches) != 1 or type(matches[0].get("id")) is not int or matches[0]["id"] <= 0:
        raise RuntimeError("Functional evidence artifact is missing, expired, or ambiguous.")
    request = urllib.request.Request(f"{base}/actions/artifacts/{matches[0]['id']}/zip", headers={
        "Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json", "User-Agent": "coglatas-final-functional-evidence",
    })
    try:
        with urllib.request.build_opener(MetadataRedirectHandler()).open(request, timeout=30) as response:
            archive = response.read(1024 * 1024 + 1)
    except (urllib.error.HTTPError, urllib.error.URLError) as error:
        raise RuntimeError("Unable to download exact-candidate Functional metadata.") from error
    digest = matches[0].get("digest")
    if not isinstance(digest, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", digest) or digest != "sha256:" + hashlib.sha256(archive).hexdigest():
        raise RuntimeError("Functional metadata does not match its GitHub artifact digest.")
    manifest = read_manifest_archive(archive)
    try:
        return functional_evidence.validate_manifest(manifest, sha, "functional-full", run_id, str(attempt))
    except (ValueError, TypeError, KeyError) as error:
        raise RuntimeError("Functional evidence failed schema, provenance, or owner validation.") from error


def main() -> int:
    repository = required_env("GITHUB_REPOSITORY")
    sha = required_env("GITHUB_SHA")
    token = required_env("GITHUB_TOKEN")
    api_url = os.environ.get("GITHUB_API_URL", "https://api.github.com").strip()
    check_runs = fetch_check_runs(repository, sha, token, api_url)
    failures, summary = evaluate_required_checks(check_runs, sha)

    manifest = None
    try:
        manifest = verify_functional_evidence(check_runs, repository, sha, token, api_url)
    except RuntimeError as error:
        failures.append(str(error))

    print("MVP-A final check evidence:")
    for name, green in summary:
        result = "PASS" if green else "FAIL"
        print(f"- {name}: {result}")

    if manifest:
        print(functional_evidence.summarize(manifest))
    decision = "NO-GO" if failures else "GO"
    print(f"MVP-A candidate decision: {decision}")
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as destination:
            destination.write(f"### MVP-A candidate: {decision}\n\nCandidate: `{sha}`\n\n")
            if manifest:
                destination.write(functional_evidence.summarize(manifest))
            destination.write("#482 integrated terminal regression and #481 public HTTPS deployment remain separate release requirements; this check does not replace either.\n")

    if failures:
        print("MVP-A final gate failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1

    print(
        f"MVP-A final gate passed: {len(REQUIRED_CHECKS)} required checks are green, "
        f"trusted, and bound to exact SHA {sha}."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1) from error
