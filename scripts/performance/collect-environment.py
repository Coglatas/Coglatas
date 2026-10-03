#!/usr/bin/env python3
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import platform
import subprocess
import sys
from pathlib import Path
from typing import Sequence

from common import (
    PerformanceContractError,
    load_json,
    repository_root,
    validate_fixture_evidence,
    write_json_atomic,
)


def run(command: Sequence[str], *, timeout: float = 60.0) -> str:
    try:
        result = subprocess.run(
            list(command),
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=timeout,
        )
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
        raise PerformanceContractError(f"fingerprint command failed: {' '.join(command)}: {exc}") from exc
    value = result.stdout.strip()
    if not value:
        raise PerformanceContractError(f"fingerprint command returned no output: {' '.join(command)}")
    return value


def compose_command(
    project: str,
    compose_file: Path,
    compose_override: Path | None,
    *args: str,
) -> list[str]:
    command = ["docker", "compose", "-p", project, "-f", str(compose_file)]
    if compose_override is not None:
        command.extend(["-f", str(compose_override)])
    command.extend(args)
    return command


def first_line(value: str) -> str:
    return value.splitlines()[0].strip()


def cpu_model() -> str:
    cpuinfo = Path("/proc/cpuinfo")
    if cpuinfo.exists():
        for line in cpuinfo.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.lower().startswith("model name") and ":" in line:
                return line.split(":", 1)[1].strip()
    return platform.processor() or "unknown"


def memory_bytes() -> int:
    meminfo = Path("/proc/meminfo")
    if meminfo.exists():
        for line in meminfo.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("MemTotal:"):
                return int(line.split()[1]) * 1024
    try:
        return int(os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES"))
    except (ValueError, OSError, AttributeError):
        raise PerformanceContractError("cannot determine runner memory")


def git_sha() -> str:
    candidate = os.environ.get("GITHUB_SHA")
    if candidate and len(candidate) >= 7:
        return candidate
    return run(["git", "rev-parse", "HEAD"])


def locked_playwright_version(root: Path) -> str:
    lock = load_json(root / "package-lock.json")
    packages = lock.get("packages")
    if not isinstance(packages, dict):
        raise PerformanceContractError("package-lock.json missing packages map")
    entry = packages.get("node_modules/@playwright/test")
    if not isinstance(entry, dict) or not isinstance(entry.get("version"), str):
        raise PerformanceContractError("package-lock.json missing locked @playwright/test version")
    return entry["version"]


def image_id(project: str, compose_file: Path, compose_override: Path | None, service: str) -> str:
    container_id = run(compose_command(project, compose_file, compose_override, "ps", "-q", service))
    if not container_id:
        raise PerformanceContractError(f"service {service} has no container id")
    return run(["docker", "inspect", "--format", "{{.Image}}", first_line(container_id)])


def configured_compose_image_id(
    project: str,
    compose_file: Path,
    compose_override: Path | None,
    service: str,
) -> str:
    rendered = run(
        compose_command(
            project,
            compose_file,
            compose_override,
            "config",
            "--format",
            "json",
        )
    )
    payload = json.loads(rendered)
    services = payload.get("services") if isinstance(payload, dict) else None
    service_config = services.get(service) if isinstance(services, dict) else None
    image_name = service_config.get("image") if isinstance(service_config, dict) else None
    if not isinstance(image_name, str) or not image_name:
        raise PerformanceContractError(f"service {service} has no configured image")
    return run(["docker", "image", "inspect", "--format", "{{.Id}}", image_name])


def main() -> int:
    parser = argparse.ArgumentParser(description="Collect machine-readable PERF-02 environment fingerprint.")
    parser.add_argument("--compose-project", required=True)
    parser.add_argument("--compose-file", type=Path, required=True)
    parser.add_argument("--compose-override", type=Path)
    parser.add_argument("--profile", choices=("small", "medium", "large"), required=True)
    parser.add_argument("--fixture-evidence", type=Path, required=True)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    try:
        if not args.compose_project.startswith("coglatas-performance-"):
            raise PerformanceContractError("Compose project must use the dedicated coglatas-performance- prefix")
        evidence = validate_fixture_evidence(load_json(args.fixture_evidence), args.profile, args.manifest)
        root = repository_root()

        postgres_version = run(compose_command(
            args.compose_project,
            args.compose_file,
            args.compose_override,
            "exec", "-T", "postgres",
            "psql", "-U", "coglatas_performance", "-d", "coglatas_performance",
            "-Atc", "SHOW server_version",
        ))
        dotnet_runtime = run(compose_command(
            args.compose_project, args.compose_file, args.compose_override, "exec", "-T", "app", "dotnet", "--info"
        ))
        dotnet_sdk = run(compose_command(
            args.compose_project, args.compose_file, args.compose_override, "run", "--rm", "--no-deps", "migrate", "dotnet", "--info"
        ))
        node_version = run(compose_command(
            args.compose_project, args.compose_file, args.compose_override, "run", "--rm", "--no-deps", "performance-browser", "node", "--version"
        ))
        npm_version = run(compose_command(
            args.compose_project, args.compose_file, args.compose_override, "run", "--rm", "--no-deps", "performance-browser", "npm", "--version"
        ))
        browser_version = run(compose_command(
            args.compose_project, args.compose_file, args.compose_override, "run", "--rm", "--no-deps", "performance-browser",
            "bash", "-lc",
            'for f in /ms-playwright/chromium-*/chrome-linux*/chrome; do '
            'if [ -x "$f" ]; then "$f" --version; exit 0; fi; done; exit 1',
        ))

        app_image = image_id(args.compose_project, args.compose_file, args.compose_override, "app")
        postgres_image = image_id(args.compose_project, args.compose_file, args.compose_override, "postgres")
        browser_image = configured_compose_image_id(
            args.compose_project,
            args.compose_file,
            args.compose_override,
            "performance-browser",
        )

        output = {
            "schemaVersion": 1,
            "phase": "environment-fingerprint",
            "capturedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "commitSha": git_sha(),
            "runner": {
                "os": platform.platform(),
                "runnerOs": os.environ.get("RUNNER_OS") or platform.system(),
                "runnerImage": os.environ.get("ImageOS") or os.environ.get("RUNNER_IMAGE") or "local",
                "cpuCount": os.cpu_count(),
                "cpuModel": cpu_model(),
                "memoryBytes": memory_bytes(),
            },
            "dotnet": {
                "sdkInfo": dotnet_sdk,
                "runtimeInfo": dotnet_runtime,
            },
            "node": {
                "version": first_line(node_version),
                "npmVersion": first_line(npm_version),
            },
            "postgresql": {
                "version": first_line(postgres_version),
            },
            "browser": {
                "playwrightVersion": locked_playwright_version(root),
                "version": first_line(browser_version),
            },
            "containerImages": {
                "app": first_line(app_image),
                "postgres": first_line(postgres_image),
                "performanceBrowser": first_line(browser_image),
            },
            "fixture": {
                "profile": args.profile,
                "seed": evidence["seed"],
                "hash": evidence["fixtureHash"],
                "version": evidence["fixtureVersion"],
            },
        }
        required_strings = [
            output["commitSha"],
            output["runner"]["os"],
            output["runner"]["cpuModel"],
            output["dotnet"]["sdkInfo"],
            output["dotnet"]["runtimeInfo"],
            output["node"]["version"],
            output["node"]["npmVersion"],
            output["postgresql"]["version"],
            output["browser"]["playwrightVersion"],
            output["browser"]["version"],
            output["containerImages"]["app"],
            output["containerImages"]["postgres"],
            output["containerImages"]["performanceBrowser"],
            output["fixture"]["hash"],
        ]
        if output["runner"]["cpuCount"] is None or output["runner"]["cpuCount"] <= 0 or output["runner"]["memoryBytes"] <= 0:
            raise PerformanceContractError("runner CPU/memory fingerprint is incomplete")
        if any(not isinstance(value, str) or not value.strip() for value in required_strings):
            raise PerformanceContractError("environment fingerprint contains a missing required field")

        write_json_atomic(args.output, output)
        print(json.dumps({
            "phase": output["phase"],
            "commitSha": output["commitSha"],
            "fixtureHash": output["fixture"]["hash"],
        }, sort_keys=True))
        return 0
    except (PerformanceContractError, KeyError, OSError, ValueError) as exc:
        print(f"PERF-02 environment fingerprint failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
