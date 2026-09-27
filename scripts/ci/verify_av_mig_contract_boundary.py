#!/usr/bin/env python3
"""Production AV-MIG boundary verifier backed by SDK Roslyn syntax parsing."""
from __future__ import annotations
import argparse
from functools import lru_cache
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any
import verify_av_mig_contract_boundary_base as base
REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_POLICY = REPO_ROOT / "docs/migration/avalonia/p0-api-boundary.json"
OPENAPI_OPERATION_METHODS = {"get", "post", "put", "patch", "delete", "options", "head", "trace"}
BoundaryViolation = base.BoundaryViolation
require = base.require
load_policy = base.load_policy

def _normalized_expected_signatures(signalr: dict[str, Any]) -> list[tuple[str, str, tuple[str, ...]]]:
    client_methods = signalr.get("clientMethods")
    signatures = signalr.get("clientMethodSignatures")
    require(
        isinstance(client_methods, list)
        and bool(client_methods)
        and all(isinstance(item, str) and item for item in client_methods)
        and len(set(client_methods)) == len(client_methods),
        "signalR.clientMethods must be a non-empty unique string array",
    )
    require(isinstance(signatures, list), "signalR.clientMethodSignatures must be an array")
    result: list[tuple[str, str, tuple[str, ...]]] = []
    for item in signatures:
        require(isinstance(item, dict), "signalR.clientMethodSignatures entries must be objects")
        name = item.get("name")
        return_type = item.get("returnType")
        parameter_types = item.get("parameterTypes")
        require(
            isinstance(name, str) and name
            and isinstance(return_type, str) and return_type
            and isinstance(parameter_types, list)
            and all(isinstance(parameter, str) and parameter for parameter in parameter_types),
            f"invalid SignalR client method signature: {item!r}",
        )
        result.append((
            name,
            base.normalize_csharp_type(return_type),
            tuple(base.normalize_csharp_type(parameter) for parameter in parameter_types),
        ))
    require(
        [item[0] for item in result] == client_methods
        and len({item[0] for item in result}) == len(result),
        "signalR.clientMethodSignatures must exactly correspond to clientMethods in order",
    )
    return result


def _anonymous_operation_allowlist(policy: dict[str, Any]) -> set[tuple[str, str]]:
    openapi_policy = policy.get("openapi")
    require(isinstance(openapi_policy, dict), "openapi policy section is missing")
    entries = openapi_policy.get("allowedAnonymousOperations")
    require(
        isinstance(entries, list) and bool(entries),
        "openapi.allowedAnonymousOperations must be a non-empty array",
    )
    allowed: set[tuple[str, str]] = set()
    for entry in entries:
        require(isinstance(entry, dict), "openapi.allowedAnonymousOperations entries must be objects")
        method = entry.get("method")
        path = entry.get("path")
        normalized = method.lower() if isinstance(method, str) else ""
        key = (normalized, path) if isinstance(path, str) else None
        require(
            normalized in OPENAPI_OPERATION_METHODS
            and isinstance(path, str)
            and path.startswith("/")
            and key not in allowed,
            f"invalid anonymous operation allowlist entry: {entry!r}",
        )
        assert key is not None
        allowed.add(key)

    required_operations = policy.get("requiredOperations")
    require(isinstance(required_operations, list), "requiredOperations must be an array")
    for entry in required_operations:
        if not isinstance(entry, dict) or entry.get("anonymous") is not True:
            continue
        method = entry.get("method")
        path = entry.get("path")
        require(
            isinstance(method, str)
            and isinstance(path, str)
            and (method.lower(), path) in allowed,
            f"required anonymous operation is not in allowedAnonymousOperations: {entry!r}",
        )
    return allowed


def effective_security(document, operation):
    value = operation['security'] if 'security' in operation else document.get('security', [])
    require(isinstance(value, list), 'OpenAPI effective security must be an array')
    known = document.get('components', {}).get('securitySchemes', {})
    for requirement in value:
        require(isinstance(requirement, dict), 'OpenAPI security requirement must be an object')
        for scheme, scopes in requirement.items():
            require(scheme in known, f'OpenAPI unknown authentication scheme: {scheme}')
            require(isinstance(scopes, list) and all(isinstance(v, str) for v in scopes), 'OpenAPI security scopes must be strings')
    return value


def allows_anonymous(security):
    return not security or any(not alternative for alternative in security)


def accepted_auth_schemes(security):
    return {name for alternative in security for name in alternative}


def verify_anonymous_openapi_allowlist(document, policy):
    allowed = _anonymous_operation_allowlist(policy)
    for path, item in document['paths'].items():
        require(isinstance(item, dict), 'OpenAPI invalid path item')
        for method, operation in item.items():
            if method.lower() not in OPENAPI_OPERATION_METHODS:
                continue
            require(isinstance(operation, dict), 'OpenAPI invalid operation')
            security = effective_security(document, operation)
            require(not allows_anonymous(security) or (method.lower(), path) in allowed,
                    f'OpenAPI operation is effectively anonymous but not explicitly allowlisted: {method.upper()} {path}')


def verify_operation_inventory(document, policy, repo_root):
    inventory = json.loads((repo_root / 'docs/migration/avalonia/p0-operation-inventory.json').read_text())
    canonical = {(e['method'].lower(), e['path']): e['anonymous'] for e in inventory['operations']}
    required = {(e['method'].lower(), e['path']): e['anonymous'] for e in policy['requiredOperations']}
    require(len(canonical) == len(inventory['operations']) and len(required) == len(policy['requiredOperations']), 'duplicate canonical/policy operation')
    require(canonical == required, 'canonical P0 operation inventory/policy mismatch')
    for method, path in canonical:
        require(method in document.get('paths', {}).get(path, {}), 'canonical P0 operation missing from OpenAPI')


@lru_cache(maxsize=1)
def _inspector_binary():
    project = REPO_ROOT / 'tools/AvMig.SourceInspector/AvMig.SourceInspector.csproj'
    result = subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '--nologo', '--disable-build-servers', '-p:UseSharedCompilation=false'], cwd=REPO_ROOT, capture_output=True, text=True, timeout=120)
    require(result.returncode == 0, 'Roslyn inspector build failed: ' + result.stdout + result.stderr)
    return project.parent / 'bin/Release/net10.0/AvMig.SourceInspector.dll'


def inspect_source(repo_root, symbols):
    result = subprocess.run(['dotnet', str(_inspector_binary()), str(repo_root), ';'.join(sorted(symbols))], capture_output=True, text=True, timeout=60)
    require(result.returncode == 0, 'C# syntax inspection failed: ' + result.stderr)
    return json.loads(result.stdout)


def verify_signalr_contract(policy, repo_root, defined_symbols, source=None):
    source = source if source is not None else inspect_source(repo_root, defined_symbols)
    signalr, hub = policy['nonOpenApiContracts']['signalR'], source['hub']
    require(hub['isPublic'] and hub['isSealed'], 'SignalR AppHub public sealed class declaration is missing')
    require(not any(a['name'] == 'AllowAnonymous' for a in hub['attributes']), 'SignalR AppHub must not allow [AllowAnonymous]')
    auth = [a for a in hub['attributes'] if a['name'] == 'Authorize']
    require(len(auth) == 1, 'SignalR AppHub must require [Authorize]')
    require(auth[0]['arguments'] is None, 'SignalR AppHub [Authorize] must inherit the pinned default authentication scheme without arguments')
    mappings = source['mappings']
    require(len(mappings) == 1 and mappings[0]['direct'] and not mappings[0]['earlyExit'] and mappings[0]['receiver'] == 'app' and mappings[0]['route'] == signalr['path'],
            'SignalR AppHub path drifted: one direct unchained app.MapHub statement; top-level unchained mapping required')
    require(source['authentication'] in (['CookieAuthenticationDefaults.AuthenticationScheme'], ['Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme']), 'SignalR default authentication scheme registration drift')
    methods = [m for m in hub['methods'] if not m['isStatic'] and not m['generic']]
    names = [m['name'] for m in methods]
    require(len(names) == len(set(names)), 'SignalR public hub method names must be unique; duplicates')
    expected_names = set(signalr['clientMethods']) | {'OnConnectedAsync', 'OnDisconnectedAsync'}
    require(not any(a['name'] == 'HubMethodName' for m in methods for a in m['attributes']), 'SignalR callable public method is missing because HubMethodName aliases are not allowed')
    for name, return_type, parameters in _normalized_expected_signatures(signalr):
        matches = [m for m in methods if m['name'] == name]
        require(len(matches) == 1, f'SignalR client method is missing; callable public method is missing from AppHub: {name}')
        require(not any(a['name'] in ('Authorize', 'AllowAnonymous') for a in matches[0]['attributes']), f'SignalR method authorization drift: {name}')
        actual = (matches[0]['returnType'], tuple(matches[0]['parameterTypes']))
        require(actual == (return_type, parameters), f'SignalR client method signature drifted for AppHub.{name}: {actual}')
    require(set(names) == expected_names and len(names) == len(expected_names), 'SignalR public Hub method surface drifted')
    for event in signalr['serverEvents']:
        require(event in source['events'], f'SignalR server event is not emitted by realtime sources: {event}')


def verify_csrf_contract(policy, repo_root, defined_symbols, source=None):
    source = source if source is not None else inspect_source(repo_root, defined_symbols)
    actual, expected = source['csrf'], policy['nonOpenApiContracts']['csrf']
    require(actual['route'] is not None and actual['action'] is not None and actual['isPublic'] and actual['anonymous'] and actual['returnType'] == 'ActionResult<CsrfTokenResponse>', 'CSRF token controller route/action contract is missing')
    endpoint = '/' + '/'.join(x.strip('/') for x in (actual['route'], actual['action']))
    require(endpoint == expected['tokenEndpoint'], 'CSRF token endpoint drifted')
    require(actual['header'] == expected['headerName'], 'CSRF header drifted')
    require(actual['responseHeader'] in ('SecurityOptions.CsrfHeaderName', 'Coglatas.Web.Configuration.SecurityOptions.CsrfHeaderName'), 'CSRF token response must expose SecurityOptions.CsrfHeaderName')


def verify_boundary(document, policy, repo_root):
    base.verify_openapi_contract(document, policy)
    verify_operation_inventory(document, policy, repo_root)
    verify_anonymous_openapi_allowlist(document, policy)
    symbols = base.resolve_effective_csharp_symbols(policy, repo_root)
    source = inspect_source(repo_root, symbols)
    verify_signalr_contract(policy, repo_root, symbols, source)
    verify_csrf_contract(policy, repo_root, symbols, source)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('openapi', type=Path)
    parser.add_argument('--repo-root', type=Path, default=REPO_ROOT)
    parser.add_argument('--policy', type=Path, default=DEFAULT_POLICY)
    args = parser.parse_args()
    os.environ.pop('AV_MIG_CSHARP_DEFINE_CONSTANTS', None)
    try:
        document = json.loads(args.openapi.read_text())
        require(isinstance(document, dict), 'OpenAPI document must be an object')
        verify_boundary(document, load_policy(args.policy), args.repo_root.resolve())
    except (OSError, UnicodeError, json.JSONDecodeError, subprocess.SubprocessError, BoundaryViolation) as error:
        print(f'AV-MIG contract boundary verification failed: {error}', file=sys.stderr)
        return 1
    print('AV-MIG contract boundary verification passed: OpenAPI, Roslyn SignalR and CSRF contracts')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
