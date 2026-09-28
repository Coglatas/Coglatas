#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
COMMON_PATH = ROOT / "scripts" / "performance" / "common.py"
spec = importlib.util.spec_from_file_location("performance_common", COMMON_PATH)
if spec is None or spec.loader is None:
    raise SystemExit("PERF-02: cannot load scripts/performance/common.py")
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)


def fail(message: str) -> None:
    raise SystemExit(f"PERF-02 contract invalid: {message}")


def require_text(text: str, token: str, source: str) -> None:
    if token not in text:
        fail(f"{source} missing required token: {token}")


def main() -> int:
    environment_path = ROOT / "performance" / "environment.json"
    compose_path = ROOT / "docker-compose.performance.yml"
    harness_path = ROOT / "scripts" / "performance" / "with-environment.sh"
    warmup_path = ROOT / "scripts" / "performance" / "warmup.py"
    seed_path = ROOT / "src" / "Coglatas.Infrastructure" / "Persistence" / "PerformanceCiFixtureSeed.cs"
    hosting_path = ROOT / "src" / "Coglatas.Web" / "Testing" / "PerformanceCiHostingStartup.cs"
    boundary_path = ROOT / "src" / "Coglatas.Web" / "Testing" / "PerformanceCiTestBoundary.cs"

    for path in (environment_path, compose_path, harness_path, warmup_path, seed_path, hosting_path, boundary_path):
        if not path.is_file():
            fail(f"missing required file: {path.relative_to(ROOT)}")

    environment = json.loads(environment_path.read_text(encoding="utf-8"))
    if environment.get("schemaVersion") != 1 or environment.get("fixtureVersion") != 1:
        fail("environment schemaVersion/fixtureVersion must be 1")
    target = environment.get("target")
    if not isinstance(target, dict):
        fail("target contract is missing")
    allowed_hosts = set(target.get("allowedHosts") or [])
    if allowed_hosts != set(common.SAFE_TARGET_HOSTS):
        fail("environment target allowlist must exactly match common.py SAFE_TARGET_HOSTS")
    if target.get("scheme") != "http" or target.get("requireExplicitPort") is not True:
        fail("target must be isolated HTTP with an explicit port")

    warmup = environment.get("warmup")
    if not isinstance(warmup, dict) or warmup.get("measured") is not False:
        fail("warm-up must be explicitly non-measured")
    if not isinstance(warmup.get("iterations"), int) or warmup["iterations"] <= 0:
        fail("warm-up iterations must be positive")
    routes = warmup.get("routes")
    if not isinstance(routes, list) or not routes or any(not isinstance(route, str) or not route.startswith("/") for route in routes):
        fail("warm-up routes must be a non-empty list of application-relative paths")
    cache = warmup.get("browserAssetCachePolicy")
    if not isinstance(cache, dict) or set(cache) != {"cold", "warm"}:
        fail("browser cache policy must state both cold and warm behavior")

    measurement = environment.get("measurement")
    if not isinstance(measurement, dict) or measurement.get("warmupSamplesExcluded") is not True:
        fail("measurement contract must exclude warm-up samples")
    if not isinstance(measurement.get("minimumSamples"), int) or measurement["minimumSamples"] <= 0:
        fail("measurement minimumSamples must be positive")

    failure_policy = environment.get("failurePolicy")
    required_failures = {
        "unhealthyApp", "incompleteFixture", "benchmarkExitOrTimeout", "insufficientSamples",
        "targetMismatch", "missingFingerprint", "unstableEnvironment",
    }
    if not isinstance(failure_policy, dict) or any(failure_policy.get(key) != "fail" for key in required_failures):
        fail("all PERF-02 failure/noise guards must fail closed")

    hashes = {}
    for profile in ("small", "medium", "large"):
        _, loaded = common.load_profile(profile)
        first_hash = common.fixture_hash(profile)
        second_hash = common.fixture_hash(profile)
        if first_hash != second_hash or not re.fullmatch(r"[0-9a-f]{64}", first_hash):
            fail(f"fixture hash for {profile} is not deterministic SHA-256")
        if loaded["counts"]["tasks"] != loaded["counts"]["workItems"]:
            fail(f"{profile} tasks/workItems contract drifted")
        hashes[profile] = first_hash
    if len(set(hashes.values())) != 3:
        fail("fixture hashes must distinguish small/medium/large profiles")

    compose = compose_path.read_text(encoding="utf-8")
    for token in (
        "image: postgres:18-alpine",
        "Database=coglatas_performance",
        '127.0.0.1:${COGLATAS_PERFORMANCE_PORT:-18080}:8080',
        "dockerfile: Dockerfile",
        "ASPNETCORE_ENVIRONMENT: Test",
        'COGLATAS_BROWSER_SMOKE_SEED_ENABLED: "false"',
        'COGLATAS_DEMO_DATASET_ENABLED: "false"',
        'COGLATAS_SECURITY_CI_FIXTURE_ENABLED: "false"',
        'COGLATAS_PERFORMANCE_CI_FIXTURE_ENABLED: "true"',
        "condition: service_completed_successfully",
        "/health/ready",
    ):
        require_text(compose, token, "docker-compose.performance.yml")
    if re.search(r"https?://(?!0\.0\.0\.0|localhost|127\.0\.0\.1)", compose, re.IGNORECASE):
        fail("performance Compose must not contain a public benchmark target")

    harness = harness_path.read_text(encoding="utf-8")
    for token in (
        "coglatas-performance-",
        "down --volumes --remove-orphans",
        "preflight.py",
        "warmup.py",
        "collect-environment.py",
        "verify-samples.py",
        'timeout "$COMMAND_TIMEOUT"',
    ):
        require_text(harness, token, "with-environment.sh")

    warmup_script = warmup_path.read_text(encoding="utf-8")
    for token in (
        "/api/security/csrf-token",
        "csrf_header: csrf_token",
        "\"csrfBootstrap\"",
    ):
        require_text(warmup_script, token, "warmup.py")

    seed = seed_path.read_text(encoding="utf-8")
    for token in (
        'DatabaseName = "coglatas_performance"',
        "NpgsqlConnectionStringBuilder",
        "AllowedDatabaseDataSources.Contains(configuredHost)",
        "GetPendingMigrationsAsync",
        "TRUNCATE TABLE",
        "StableGuid",
        "VerifyFixtureAsync",
        "fixtureHash",
        "migrationStatus = \"current\"",
    ):
        require_text(seed, token, "PerformanceCiFixtureSeed.cs")

    hosting = hosting_path.read_text(encoding="utf-8")
    for token in (
        "PerformanceCiTestBoundary.IsEnabled",
        "IHostedLifecycleService",
        "StartingAsync",
        "SetPlatformScope",
        "PerformanceCiFixtureSeed.SeedAsync",
    ):
        require_text(hosting, token, "PerformanceCiHostingStartup.cs")

    boundary = boundary_path.read_text(encoding="utf-8")
    require_text(boundary, 'string.Equals(environmentName, "Test"', "PerformanceCiTestBoundary.cs")

    print(json.dumps({
        "schemaVersion": 1,
        "profiles": hashes,
        "warmupRoutes": len(routes),
        "minimumSamples": measurement["minimumSamples"],
        "status": "ok",
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
