#!/usr/bin/env python3
from __future__ import annotations

import argparse
import datetime as dt
import http.cookiejar
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

from common import (
    PerformanceContractError,
    load_json,
    repository_root,
    validate_fixture_evidence,
    validate_target,
    write_json_atomic,
)


def open_request(opener: urllib.request.OpenerDirector, request: urllib.request.Request, timeout: float) -> tuple[int, float]:
    started = time.perf_counter()
    try:
        with opener.open(request, timeout=timeout) as response:
            response.read()
            status = response.status
    except urllib.error.HTTPError as exc:
        exc.read()
        status = exc.code
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    return status, elapsed_ms


def main() -> int:
    parser = argparse.ArgumentParser(description="Execute non-measured PERF-02 warm-up samples.")
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--profile", choices=("small", "medium", "large"), required=True)
    parser.add_argument("--fixture-evidence", type=Path, required=True)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--environment-contract", type=Path, default=repository_root() / "performance" / "environment.json")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=20.0)
    args = parser.parse_args()

    try:
        base_url = validate_target(args.base_url)
        evidence = validate_fixture_evidence(load_json(args.fixture_evidence), args.profile, args.manifest)
        contract = load_json(args.environment_contract)
        warmup_contract = contract.get("warmup")
        if not isinstance(warmup_contract, dict) or warmup_contract.get("measured") is not False:
            raise PerformanceContractError("warm-up contract must explicitly set measured=false")
        iterations = warmup_contract.get("iterations")
        routes = warmup_contract.get("routes")
        if not isinstance(iterations, int) or iterations <= 0:
            raise PerformanceContractError("warm-up iterations must be a positive integer")
        if not isinstance(routes, list) or not routes:
            raise PerformanceContractError("warm-up contract requires at least one route")

        password = os.environ.get("COGLATAS_PERFORMANCE_PASSWORD")
        if not password:
            raise PerformanceContractError("COGLATAS_PERFORMANCE_PASSWORD is required for warm-up authentication")
        identities = evidence["identities"]
        tenant_slug = identities["tenantSlug"]
        operator_email = identities["operatorEmail"]

        cookie_jar = http.cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookie_jar))

        csrf_request = urllib.request.Request(
            f"{base_url}/api/security/csrf-token",
            headers={"X-Tenant-Slug": tenant_slug},
            method="GET",
        )
        csrf_started = time.perf_counter()
        try:
            with opener.open(csrf_request, timeout=args.timeout_seconds) as response:
                csrf_body = response.read()
                csrf_status = response.status
        except urllib.error.HTTPError as exc:
            csrf_body = exc.read()
            csrf_status = exc.code
        csrf_ms = (time.perf_counter() - csrf_started) * 1000.0
        if csrf_status != 200:
            raise PerformanceContractError(f"warm-up CSRF bootstrap failed with HTTP {csrf_status}")

        try:
            csrf_payload = json.loads(csrf_body)
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            raise PerformanceContractError("warm-up CSRF bootstrap returned invalid JSON") from exc
        csrf_token = csrf_payload.get("token")
        csrf_header = csrf_payload.get("headerName")
        if not isinstance(csrf_token, str) or not csrf_token or not isinstance(csrf_header, str) or not csrf_header:
            raise PerformanceContractError("warm-up CSRF bootstrap returned an incomplete token payload")

        login_body = json.dumps({"email": operator_email, "password": password}).encode("utf-8")
        login_request = urllib.request.Request(
            f"{base_url}/api/auth/login",
            data=login_body,
            headers={
                "Content-Type": "application/json",
                "X-Tenant-Slug": tenant_slug,
                csrf_header: csrf_token,
            },
            method="POST",
        )
        login_status, login_ms = open_request(opener, login_request, args.timeout_seconds)
        if login_status != 200:
            raise PerformanceContractError(f"warm-up login failed with HTTP {login_status}")

        route_values = {
            "workspaceId": identities["workspaceId"],
            "taskListProjectId": identities["taskListProjectId"],
            "ganttProjectId": identities["ganttProjectId"],
            "kanbanProjectId": identities["kanbanProjectId"],
        }
        samples: list[dict[str, object]] = []
        headers = {"X-Tenant-Slug": tenant_slug}
        for iteration in range(iterations):
            for route_template in routes:
                if not isinstance(route_template, str) or not route_template.startswith("/"):
                    raise PerformanceContractError(f"invalid warm-up route {route_template!r}")
                route = route_template.format(**route_values)
                request = urllib.request.Request(f"{base_url}{route}", headers=headers)
                status, elapsed_ms = open_request(opener, request, args.timeout_seconds)
                if status < 200 or status >= 400:
                    raise PerformanceContractError(f"warm-up route {route} failed with HTTP {status}")
                samples.append({
                    "phase": "warmup",
                    "measured": False,
                    "iteration": iteration + 1,
                    "route": route,
                    "status": status,
                    "elapsedMs": round(elapsed_ms, 3),
                })

        output = {
            "schemaVersion": 1,
            "phase": "warmup",
            "measured": False,
            "completedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "profile": args.profile,
            "fixtureHash": evidence["fixtureHash"],
            "csrfBootstrap": {"status": csrf_status, "elapsedMs": round(csrf_ms, 3), "measured": False},
            "login": {"status": login_status, "elapsedMs": round(login_ms, 3), "measured": False},
            "sampleCount": len(samples),
            "samples": samples,
            "browserAssetCachePolicy": warmup_contract.get("browserAssetCachePolicy"),
        }
        write_json_atomic(args.output, output)
        print(json.dumps({"phase": "warmup", "sampleCount": len(samples), "measured": False}, sort_keys=True))
        return 0
    except (PerformanceContractError, KeyError, OSError, ValueError) as exc:
        print(f"PERF-02 warm-up failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
