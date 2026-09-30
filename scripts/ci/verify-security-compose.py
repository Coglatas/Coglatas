#!/usr/bin/env python3
"""Fail closed when the resolved SEC-02 security Compose profile drifts."""

from __future__ import annotations

import json
import sys
from typing import Any


def fail(message: str) -> None:
    raise SystemExit(f"SEC-02 Compose invariant failed: {message}")


def require_mapping(value: Any, path: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{path} must be an object")
    return value


def require_service(document: dict[str, Any], name: str) -> dict[str, Any]:
    services = require_mapping(document.get("services"), "services")
    service = services.get(name)
    if not isinstance(service, dict):
        fail(f"services.{name} is missing")
    return service


def require_environment(service: dict[str, Any], service_name: str) -> dict[str, Any]:
    environment = service.get("environment")
    if not isinstance(environment, dict):
        fail(f"services.{service_name}.environment must be an object")
    return environment


def require_env(environment: dict[str, Any], key: str, expected: str) -> None:
    actual = environment.get(key)
    if str(actual).lower() != expected.lower():
        fail(f"{key} expected {expected!r}, got {actual!r}")


def main() -> None:
    try:
        document = json.load(sys.stdin)
    except json.JSONDecodeError as exc:
        fail(f"resolved Compose JSON is invalid: {exc}")

    document = require_mapping(document, "root")
    services = require_mapping(document.get("services"), "services")
    app = require_service(document, "app")
    app_env = require_environment(app, "app")

    expected_app = {
        "ASPNETCORE_ENVIRONMENT": "Test",
        "Tenancy__AppMode": "SaaS",
        "Tenancy__DefaultTenantSlug": "security-alpha",
        "Tenancy__TenantResolutionStrategy": "HeaderForDevelopmentOnly",
        "Tenancy__AllowTenantSwitching": "true",
        "Tenancy__AllowDevelopmentHeaderTenantResolution": "true",
        "Tenancy__AllowDevelopmentHeaderInProduction": "false",
        "Tenancy__DevelopmentTenantHeaderName": "X-Tenant-Slug",
        "Tenancy__SeedOnStartup": "false",
        "UiShell__SeedOnStartup": "false",
        "COGLATAS_BROWSER_SMOKE_SEED_ENABLED": "false",
        "COGLATAS_BROWSER_SMOKE_RESPONSE_GATE_ENABLED": "false",
        "COGLATAS_DEMO_DATASET_ENABLED": "false",
        "COGLATAS_SECURITY_CI_FIXTURE_ENABLED": "true",
        "Security__RequireHttps": "false",
        "Security__EnableHsts": "false",
        "Security__EnableCsrfProtection": "true",
        "Security__EnableRateLimiting": "false",
    }
    for key, expected in expected_app.items():
        require_env(app_env, key, expected)

    fixture_password = app_env.get("COGLATAS_SECURITY_CI_PASSWORD")
    if not isinstance(fixture_password, str) or not fixture_password.strip():
        fail("COGLATAS_SECURITY_CI_PASSWORD must resolve to a non-empty synthetic credential")

    if app.get("ports"):
        fail("security app must not publish a host port before a scanner explicitly needs one")

    migrate = require_service(document, "migrate")
    migrate_env = require_environment(migrate, "migrate")
    connection = str(migrate_env.get("ConnectionStrings__DefaultConnection", ""))
    if "Database=coglatas_security" not in connection:
        fail("migrate service must target the isolated coglatas_security database")

    postgres = require_service(document, "postgres")
    postgres_env = require_environment(postgres, "postgres")
    require_env(postgres_env, "POSTGRES_DB", "coglatas_security")

    # Docker Compose removes services behind inactive profiles from the default
    # resolved model. For the SEC-02 scanner stack, the inherited browser smoke
    # runner must therefore be absent unless the caller explicitly opts into its
    # `browser-smoke` profile. If it appears here, it would auto-start and pollute
    # the isolated security fixture with the unrelated browser-smoke workflow.
    if "real-backend-playwright" in services:
        fail("real-backend-playwright must be inactive in the default SEC-02 profile")

    print("SEC-02 resolved Compose invariants verified.")


if __name__ == "__main__":
    main()
