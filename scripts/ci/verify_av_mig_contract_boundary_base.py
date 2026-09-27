#!/usr/bin/env python3
"""Verify the AV-MIG-02 client-independent P0 contract boundary."""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_POLICY = REPO_ROOT / "docs/migration/avalonia/p0-api-boundary.json"


class BoundaryViolation(RuntimeError):
    """Raised when the pinned migration contract drifts."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise BoundaryViolation(message)


def load_policy(path: Path) -> dict[str, Any]:
    try:
        policy = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BoundaryViolation(f"boundary policy is not valid UTF-8 JSON: {exc}") from exc
    require(isinstance(policy, dict) and policy.get("version") == 1,
            "boundary policy must be an object with version=1")
    return policy


def verify_operation_security(
    operation: dict[str, Any],
    scheme_name: str,
    anonymous: bool,
    method: str,
    path: str,
    operation_id: str,
) -> None:
    """Verify operation-level security using OpenAPI Security Requirement OR semantics."""
    security = operation.get("security")
    description = f"{method.upper()} {path} ({operation_id})"

    if anonymous:
        require(
            security == [],
            f"anonymous operation must explicitly declare security: []: {description}",
        )
        return

    require(
        isinstance(security, list) and bool(security),
        f"protected operation must explicitly declare non-empty security: {description}",
    )
    for index, requirement in enumerate(security):
        require(
            isinstance(requirement, dict) and scheme_name in requirement,
            f"protected operation security alternative {index} must require "
            f"{scheme_name}: {description}",
        )

    require(security == [{scheme_name: []}], f"protected operation must use exactly the pinned security scheme: {description}")


def verify_openapi_contract(document: dict[str, Any], policy: dict[str, Any]) -> None:
    openapi_policy = policy.get("openapi")
    require(isinstance(openapi_policy, dict), "openapi policy section is missing")

    required_version_prefix = openapi_policy.get("requiredVersionPrefix")
    actual_version = document.get("openapi")
    require(
        isinstance(required_version_prefix, str)
        and isinstance(actual_version, str)
        and actual_version.startswith(required_version_prefix),
        f"OpenAPI version must start with {required_version_prefix!r}, got {actual_version!r}",
    )

    security_policy = openapi_policy.get("requiredSecurityScheme")
    components = document.get("components")
    security_schemes = components.get("securitySchemes") if isinstance(components, dict) else None
    require(isinstance(security_policy, dict) and isinstance(security_schemes, dict),
            "CookieAuth policy or OpenAPI securitySchemes are missing")

    scheme_name = security_policy.get("name")
    require(isinstance(scheme_name, str) and bool(scheme_name),
            "required security scheme name is invalid")
    scheme = security_schemes.get(scheme_name)
    require(isinstance(scheme, dict), f"required security scheme is missing: {scheme_name!r}")

    expected_security = {
        "type": security_policy.get("type"),
        "in": security_policy.get("in"),
        "name": security_policy.get("parameterName"),
    }
    actual_security = {
        "type": scheme.get("type"),
        "in": scheme.get("in"),
        "name": scheme.get("name"),
    }
    require(
        actual_security == expected_security,
        f"CookieAuth scheme drifted: expected {expected_security!r}, got {actual_security!r}",
    )

    paths = document.get("paths")
    require(isinstance(paths, dict), "OpenAPI paths are missing")
    required_operations = policy.get("requiredOperations")
    require(isinstance(required_operations, list) and bool(required_operations),
            "requiredOperations must be a non-empty array")

    seen_ids: set[str] = set()
    for entry in required_operations:
        require(isinstance(entry, dict), "requiredOperations entries must be objects")
        operation_id = entry.get("id")
        path = entry.get("path")
        method = entry.get("method")
        anonymous = entry.get("anonymous")
        valid = (
            isinstance(operation_id, str)
            and bool(operation_id)
            and operation_id not in seen_ids
            and isinstance(path, str)
            and path.startswith("/api/")
            and isinstance(method, str)
            and method.lower() in {"get", "post", "put", "patch", "delete"}
            and isinstance(anonymous, bool)
        )
        require(valid, f"invalid required operation entry: {entry!r}")
        seen_ids.add(operation_id)

        path_item = paths.get(path)
        operation = path_item.get(method.lower()) if isinstance(path_item, dict) else None
        responses = operation.get("responses") if isinstance(operation, dict) else None
        require(
            isinstance(operation, dict) and isinstance(responses, dict) and bool(responses),
            f"required operation is missing: {method.upper()} {path} ({operation_id})",
        )

        verify_operation_security(
            operation,
            scheme_name,
            anonymous,
            method,
            path,
            operation_id,
        )


def read_source(repo_root: Path, relative_path: str) -> str:
    path = repo_root / relative_path
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise BoundaryViolation(f"required source file is unreadable: {relative_path}: {exc}") from exc


def strip_csharp_comments(source: str) -> str:
    """Remove C# line/block comments while preserving executable source text."""
    output: list[str] = []
    index = 0
    length = len(source)
    state = "code"

    while index < length:
        char = source[index]
        next_char = source[index + 1] if index + 1 < length else ""

        if state == "line_comment":
            if char in "\r\n":
                output.append(char)
                state = "code"
            else:
                output.append(" ")
            index += 1
            continue

        if state == "block_comment":
            if char == "*" and next_char == "/":
                output.extend((" ", " "))
                index += 2
                state = "code"
                continue
            output.append(char if char in "\r\n" else " ")
            index += 1
            continue

        if state == "string":
            output.append(char)
            if char == "\\" and index + 1 < length:
                output.append(source[index + 1])
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue

        if state == "verbatim_string":
            output.append(char)
            if char == '"' and next_char == '"':
                output.append(next_char)
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue

        if state == "char":
            output.append(char)
            if char == "\\" and index + 1 < length:
                output.append(source[index + 1])
                index += 2
                continue
            if char == "'":
                state = "code"
            index += 1
            continue

        if char == "/" and next_char == "/":
            output.extend((" ", " "))
            index += 2
            state = "line_comment"
            continue
        if char == "/" and next_char == "*":
            output.extend((" ", " "))
            index += 2
            state = "block_comment"
            continue
        if char == '"':
            output.append(char)
            state = "verbatim_string" if index > 0 and source[index - 1] == "@" else "string"
            index += 1
            continue
        if char == "'":
            output.append(char)
            state = "char"
            index += 1
            continue

        output.append(char)
        index += 1

    return "".join(output)


_PREPROCESSOR_TOKEN = re.compile(r"\s*(\|\||&&|==|!=|!|\(|\)|true\b|false\b|[A-Za-z_]\w*)")


def evaluate_csharp_preprocessor_expression(expression: str, symbols: set[str]) -> bool:
    """Evaluate the boolean expression subset accepted by C# #if/#elif."""
    tokens: list[str] = []
    position = 0
    while position < len(expression):
        match = _PREPROCESSOR_TOKEN.match(expression, position)
        require(match is not None, f"unsupported C# preprocessor expression: {expression!r}")
        tokens.append(match.group(1))
        position = match.end()
    index = 0

    def peek() -> str | None:
        return tokens[index] if index < len(tokens) else None

    def consume(expected: str | None = None) -> str:
        nonlocal index
        token = peek()
        require(token is not None, f"incomplete C# preprocessor expression: {expression!r}")
        if expected is not None:
            require(token == expected, f"invalid C# preprocessor expression: {expression!r}")
        index += 1
        return token

    def parse_primary() -> bool:
        token = peek()
        if token == "(":
            consume("(")
            value = parse_or()
            consume(")")
            return value
        token = consume()
        if token == "true":
            return True
        if token == "false":
            return False
        return token in symbols

    def parse_unary() -> bool:
        if peek() == "!":
            consume("!")
            return not parse_unary()
        return parse_primary()

    def parse_equality() -> bool:
        value = parse_unary()
        while peek() in {"==", "!="}:
            operator = consume()
            right = parse_unary()
            value = value == right if operator == "==" else value != right
        return value

    def parse_and() -> bool:
        value = parse_equality()
        while peek() == "&&":
            consume("&&")
            right = parse_equality()
            value = value and right
        return value

    def parse_or() -> bool:
        value = parse_and()
        while peek() == "||":
            consume("||")
            right = parse_and()
            value = value or right
        return value

    result = parse_or()
    require(index == len(tokens), f"invalid C# preprocessor expression: {expression!r}")
    return result


def preprocess_csharp_conditionals(source: str, defined_symbols: set[str]) -> str:
    """Blank inactive conditional-compilation regions while preserving line shape."""
    symbols = set(defined_symbols)
    output: list[str] = []
    stack: list[dict[str, bool]] = []
    active = True

    for line in source.splitlines(keepends=True):
        directive_match = re.match(
            r"^\s*#\s*(if|elif|else|endif|define|undef)\b(?:\s+(.*?))?\s*(?:\r?\n)?$",
            line,
        )
        if directive_match is not None:
            directive = directive_match.group(1)
            argument = (directive_match.group(2) or "").strip()
            if directive == "if":
                condition = evaluate_csharp_preprocessor_expression(argument, symbols)
                frame = {
                    "parent_active": active,
                    "branch_taken": condition,
                    "current_active": active and condition,
                    "seen_else": False,
                }
                stack.append(frame)
                active = frame["current_active"]
            elif directive == "elif":
                require(bool(stack), "C# #elif without matching #if")
                frame = stack[-1]
                require(not frame["seen_else"], "C# #elif after #else")
                condition = evaluate_csharp_preprocessor_expression(argument, symbols)
                selected = not frame["branch_taken"] and condition
                frame["branch_taken"] = frame["branch_taken"] or condition
                frame["current_active"] = frame["parent_active"] and selected
                active = frame["current_active"]
            elif directive == "else":
                require(bool(stack), "C# #else without matching #if")
                frame = stack[-1]
                require(not frame["seen_else"], "duplicate C# #else")
                frame["seen_else"] = True
                selected = not frame["branch_taken"]
                frame["branch_taken"] = True
                frame["current_active"] = frame["parent_active"] and selected
                active = frame["current_active"]
            elif directive == "endif":
                require(bool(stack), "C# #endif without matching #if")
                stack.pop()
                active = stack[-1]["current_active"] if stack else True
            elif directive in {"define", "undef"}:
                require(re.fullmatch(r"[A-Za-z_]\w*", argument) is not None,
                        f"invalid C# #{directive} symbol: {argument!r}")
                if active:
                    if directive == "define":
                        symbols.add(argument)
                    else:
                        symbols.discard(argument)
            output.append("".join("\n" if char == "\n" else "\r" if char == "\r" else " " for char in line))
            continue

        if active:
            output.append(line)
        else:
            output.append("".join("\n" if char == "\n" else "\r" if char == "\r" else " " for char in line))

    require(not stack, "unterminated C# conditional-compilation region")
    return "".join(output)


def resolve_effective_csharp_symbols(policy: dict[str, Any], repo_root: Path) -> set[str]:
    """Resolve the same DefineConstants used by the Release contract build."""
    override = os.environ.get("AV_MIG_CSHARP_DEFINE_CONSTANTS")
    if override is not None:
        return {item.strip() for item in override.split(";") if item.strip()}

    source_build = policy.get("sourceBuild")
    require(isinstance(source_build, dict), "sourceBuild policy section is missing")
    project = source_build.get("project")
    configuration = source_build.get("configuration")
    target_framework = source_build.get("targetFramework")
    require(
        isinstance(project, str) and bool(project)
        and isinstance(configuration, str) and bool(configuration)
        and isinstance(target_framework, str) and bool(target_framework),
        "sourceBuild project/configuration/targetFramework are required",
    )

    project_path = repo_root / project
    require(project_path.is_file(), f"sourceBuild project is missing: {project}")
    command = [
        "dotnet",
        "msbuild",
        str(project_path),
        "-nologo",
        "-getProperty:DefineConstants",
        f"-p:Configuration={configuration}",
        f"-p:TargetFramework={target_framework}",
    ]
    try:
        completed = subprocess.run(
            command,
            cwd=repo_root,
            check=False,
            capture_output=True,
            text=True,
            timeout=60,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise BoundaryViolation(f"failed to resolve effective C# build symbols: {exc}") from exc
    require(
        completed.returncode == 0,
        "failed to resolve effective C# build symbols from MSBuild: "
        + (completed.stderr.strip() or completed.stdout.strip()),
    )
    lines = [line.strip() for line in completed.stdout.splitlines() if line.strip()]
    require(bool(lines), "MSBuild returned no DefineConstants for the source build")
    return {item.strip() for item in lines[-1].split(";") if item.strip()}


def read_live_csharp_source(
    repo_root: Path,
    relative_path: str,
    defined_symbols: set[str],
) -> str:
    source = strip_csharp_comments(read_source(repo_root, relative_path))
    return preprocess_csharp_conditionals(source, defined_symbols)


def strip_csharp_string_and_char_literals(source: str) -> str:
    """Blank C# string/char literal contents while preserving non-literal text."""
    output: list[str] = []
    index = 0
    length = len(source)
    state = "code"
    raw_quote_count = 0

    while index < length:
        char = source[index]
        next_char = source[index + 1] if index + 1 < length else ""

        if state == "string":
            output.append(char if char in "\r\n" else " ")
            if char == "\\" and index + 1 < length:
                output.append(source[index + 1] if source[index + 1] in "\r\n" else " ")
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue

        if state == "verbatim_string":
            output.append(char if char in "\r\n" else " ")
            if char == '"' and next_char == '"':
                output.append(next_char if next_char in "\r\n" else " ")
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue

        if state == "raw_string":
            output.append(char if char in "\r\n" else " ")
            if char == '"':
                quote_count = 1
                while index + quote_count < length and source[index + quote_count] == '"':
                    output.append(" ")
                    quote_count += 1
                if quote_count >= raw_quote_count:
                    state = "code"
                index += quote_count
                continue
            index += 1
            continue

        if state == "char":
            output.append(char if char in "\r\n" else " ")
            if char == "\\" and index + 1 < length:
                output.append(source[index + 1] if source[index + 1] in "\r\n" else " ")
                index += 2
                continue
            if char == "'":
                state = "code"
            index += 1
            continue

        if char == "$":
            dollar_index = index
            while dollar_index < length and source[dollar_index] == "$":
                dollar_index += 1
            if source.startswith('"""', dollar_index):
                quote_index = dollar_index
                while quote_index < length and source[quote_index] == '"':
                    quote_index += 1
                raw_quote_count = quote_index - dollar_index
                output.extend(" " for _ in range(quote_index - index))
                index = quote_index
                state = "raw_string"
                continue
            if dollar_index < length and source[dollar_index] == "@":
                if dollar_index + 1 < length and source[dollar_index + 1] == '"':
                    output.extend(" " for _ in range(dollar_index + 2 - index))
                    index = dollar_index + 2
                    state = "verbatim_string"
                    continue
            elif dollar_index < length and source[dollar_index] == '"':
                output.extend(" " for _ in range(dollar_index + 1 - index))
                index = dollar_index + 1
                state = "string"
                continue

        if char == "@" and next_char == "$" and index + 2 < length and source[index + 2] == '"':
            output.extend((" ", " ", " "))
            index += 3
            state = "verbatim_string"
            continue
        if char == "@" and next_char == '"':
            output.extend((" ", " "))
            index += 2
            state = "verbatim_string"
            continue
        if char == '"' and source.startswith('"""', index):
            quote_index = index
            while quote_index < length and source[quote_index] == '"':
                quote_index += 1
            raw_quote_count = quote_index - index
            output.extend(" " for _ in range(raw_quote_count))
            index = quote_index
            state = "raw_string"
            continue
        if char == '"':
            output.append(" ")
            state = "string"
            index += 1
            continue
        if char == "'":
            output.append(" ")
            state = "char"
            index += 1
            continue

        output.append(char)
        index += 1

    return "".join(output)


def extract_csharp_class_body(source: str, class_name: str) -> str:
    """Return one named C# class body while ignoring braces inside literals."""
    declarations = list(re.finditer(rf"\bclass\s+{re.escape(class_name)}\b", source))
    require(
        len(declarations) == 1,
        f"SignalR {class_name} class declaration must appear exactly once",
    )

    body_start = source.find("{", declarations[0].end())
    require(body_start >= 0, f"SignalR {class_name} class body is missing")

    depth = 1
    index = body_start + 1
    state = "code"
    length = len(source)
    while index < length:
        char = source[index]
        next_char = source[index + 1] if index + 1 < length else ""

        if state == "string":
            if char == "\\" and index + 1 < length:
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue

        if state == "verbatim_string":
            if char == '"' and next_char == '"':
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue

        if state == "char":
            if char == "\\" and index + 1 < length:
                index += 2
                continue
            if char == "'":
                state = "code"
            index += 1
            continue

        if char == '"':
            is_verbatim = (
                (index > 0 and source[index - 1] == "@")
                or (index > 1 and source[index - 2:index] == "@$")
            )
            state = "verbatim_string" if is_verbatim else "string"
            index += 1
            continue
        if char == "'":
            state = "char"
            index += 1
            continue
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[body_start + 1:index]
        index += 1

    raise BoundaryViolation(f"SignalR {class_name} class body is not balanced")


def csharp_top_level_text(source: str) -> str:
    """Keep direct-member headers while blanking nested blocks and method bodies."""
    output: list[str] = []
    depth = 0
    index = 0
    state = "code"
    while index < len(source):
        char = source[index]
        next_char = source[index + 1] if index + 1 < len(source) else ""
        visible = depth == 0

        if state == "string":
            output.append(char if char in "\r\n" else " ")
            if char == "\\" and index + 1 < len(source):
                output.append(" ")
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue
        if state == "verbatim_string":
            output.append(char if char in "\r\n" else " ")
            if char == '"' and next_char == '"':
                output.append(" ")
                index += 2
                continue
            if char == '"':
                state = "code"
            index += 1
            continue
        if state == "char":
            output.append(char if char in "\r\n" else " ")
            if char == "\\" and index + 1 < len(source):
                output.append(" ")
                index += 2
                continue
            if char == "'":
                state = "code"
            index += 1
            continue

        if char == '"':
            is_verbatim = (
                (index > 0 and source[index - 1] == "@")
                or (index > 1 and source[index - 2:index] == "@$")
            )
            state = "verbatim_string" if is_verbatim else "string"
            output.append(" ")
            index += 1
            continue
        if char == "'":
            state = "char"
            output.append(" ")
            index += 1
            continue
        if char == "{":
            output.append(char if visible else " ")
            depth += 1
            index += 1
            continue
        if char == "}":
            depth = max(0, depth - 1)
            output.append(char if depth == 0 else " ")
            index += 1
            continue

        output.append(char if visible or char in "\r\n" else " ")
        index += 1

    return "".join(output)


def normalize_csharp_type(type_name: str) -> str:
    return re.sub(r"\s+", "", type_name)


def split_csharp_parameters(parameters: str) -> list[str]:
    if not parameters.strip():
        return []
    result: list[str] = []
    start = 0
    depth = 0
    for index, char in enumerate(parameters):
        if char in "<[((":
            depth += 1
        elif char in ">])":
            depth = max(0, depth - 1)
        elif char == "," and depth == 0:
            result.append(parameters[start:index].strip())
            start = index + 1
    result.append(parameters[start:].strip())
    return result


def csharp_parameter_type(parameter: str) -> str:
    value = parameter.split("=", 1)[0].strip()
    while value.startswith("["):
        closing = value.find("]")
        require(closing >= 0, f"invalid C# parameter attribute: {parameter!r}")
        value = value[closing + 1:].strip()
    match = re.match(r"(?P<type>.+\S)\s+[A-Za-z_]\w*$", value)
    require(match is not None, f"unable to parse C# parameter: {parameter!r}")
    return normalize_csharp_type(match.group("type"))


def extract_direct_public_method_signatures(
    class_body: str,
) -> dict[str, set[tuple[str, tuple[str, ...]]]]:
    direct = csharp_top_level_text(class_body)
    pattern = re.compile(
        r"\bpublic\s+"
        r"(?:(?:static|virtual|override|sealed|async|new|unsafe|extern|partial)\s+)*"
        r"(?P<return>[^(){};=\r\n]+?)\s+"
        r"(?P<name>[A-Za-z_]\w*)\s*"
        r"\((?P<parameters>[^()]*)\)",
        flags=re.MULTILINE,
    )
    methods: dict[str, set[tuple[str, tuple[str, ...]]]] = {}
    for match in pattern.finditer(direct):
        return_type = normalize_csharp_type(match.group("return"))
        parameter_types = tuple(
            csharp_parameter_type(item)
            for item in split_csharp_parameters(match.group("parameters"))
        )
        methods.setdefault(match.group("name"), set()).add((return_type, parameter_types))
    return methods


def verify_signalr_contract(
    policy: dict[str, Any],
    repo_root: Path,
    defined_symbols: set[str],
) -> None:
    contracts = policy.get("nonOpenApiContracts")
    signalr = contracts.get("signalR") if isinstance(contracts, dict) else None
    require(isinstance(signalr, dict), "nonOpenApiContracts.signalR is missing")

    expected_path = signalr.get("path")
    authorization = signalr.get("authorization")
    client_methods = signalr.get("clientMethods")
    client_signatures = signalr.get("clientMethodSignatures")
    server_events = signalr.get("serverEvents")
    require(isinstance(expected_path, str) and expected_path.startswith("/"),
            "signalR.path must be an absolute path")
    require(
        isinstance(authorization, dict)
        and authorization.get("requiredHubAttribute") == "Authorize",
        "signalR.authorization.requiredHubAttribute must be 'Authorize'",
    )
    require(
        isinstance(client_methods, list)
        and bool(client_methods)
        and all(isinstance(item, str) and item for item in client_methods)
        and len(set(client_methods)) == len(client_methods),
        "signalR.clientMethods must be a non-empty unique string array",
    )
    require(
        isinstance(client_signatures, list) and bool(client_signatures),
        "signalR.clientMethodSignatures must be a non-empty array",
    )

    normalized_signatures: list[tuple[str, str, tuple[str, ...]]] = []
    for item in client_signatures:
        require(isinstance(item, dict), "signalR.clientMethodSignatures entries must be objects")
        name = item.get("name")
        return_type = item.get("returnType")
        parameter_types = item.get("parameterTypes")
        require(
            isinstance(name, str) and bool(name)
            and isinstance(return_type, str) and bool(return_type)
            and isinstance(parameter_types, list)
            and all(isinstance(parameter, str) and parameter for parameter in parameter_types),
            f"invalid SignalR client method signature: {item!r}",
        )
        normalized_signatures.append(
            (
                name,
                normalize_csharp_type(return_type),
                tuple(normalize_csharp_type(parameter) for parameter in parameter_types),
            )
        )
    signature_names = [item[0] for item in normalized_signatures]
    require(
        signature_names == client_methods and len(set(signature_names)) == len(signature_names),
        "signalR.clientMethodSignatures must exactly correspond to clientMethods in order",
    )
    require(
        isinstance(server_events, list)
        and bool(server_events)
        and all(isinstance(item, str) and item for item in server_events)
        and len(set(server_events)) == len(server_events),
        "signalR.serverEvents must be a non-empty unique string array",
    )

    program = read_live_csharp_source(repo_root, "src/Coglatas.Web/Program.cs", defined_symbols)
    hub_source = read_live_csharp_source(
        repo_root,
        "src/Coglatas.Web/Realtime/AppHub.cs",
        defined_symbols,
    )
    hub_declaration = re.search(
        r"(?P<attributes>(?:\s*\[[^\]]+\])*)\s*public\s+sealed\s+class\s+AppHub\b",
        hub_source,
        flags=re.MULTILINE,
    )
    require(hub_declaration is not None, "SignalR AppHub public sealed class declaration is missing")
    attributes = hub_declaration.group("attributes")
    require(
        re.search(r"\[\s*Authorize(?:Attribute)?(?:\s*\([^\]]*\))?\s*\]", attributes) is not None,
        "SignalR AppHub must require [Authorize]",
    )

    hub = extract_csharp_class_body(hub_source, "AppHub")
    direct_methods = extract_direct_public_method_signatures(hub)
    realtime_dir = repo_root / "src/Coglatas.Web/Realtime"
    require(realtime_dir.is_dir(), "Realtime source directory is missing")

    mapped_paths: list[str] = []
    literal_stripped_program = strip_csharp_string_and_char_literals(program)
    for mapping in re.finditer(
        r"MapHub\s*<\s*AppHub\s*>\s*\(",
        literal_stripped_program,
    ):
        match = re.match(
            r'MapHub\s*<\s*AppHub\s*>\s*\(\s*"([^"]+)"\s*\)',
            program[mapping.start():],
        )
        if match is not None:
            mapped_paths.append(match.group(1))
    require(
        mapped_paths == [expected_path],
        f"SignalR AppHub path drifted: expected {[expected_path]!r}, got {mapped_paths!r}",
    )

    for method, return_type, parameter_types in normalized_signatures:
        actual_signatures = direct_methods.get(method)
        require(
            actual_signatures is not None,
            f"SignalR client method is missing from AppHub: {method}",
        )
        expected_signature = (return_type, parameter_types)
        require(
            expected_signature in actual_signatures,
            f"SignalR client method signature drifted for AppHub.{method}: "
            f"expected {expected_signature!r}, got {sorted(actual_signatures)!r}",
        )

    emitted_events: set[str] = set()
    for source_path in sorted(realtime_dir.glob("*.cs")):
        source = read_live_csharp_source(
            repo_root,
            str(source_path.relative_to(repo_root)),
            defined_symbols,
        )
        emitted_events.update(
            re.findall(r'\.SendAsync\(\s*"([^"]+)"', source, flags=re.MULTILINE)
        )

    for event_name in server_events:
        require(
            event_name in emitted_events,
            f"SignalR server event is not emitted by realtime sources: {event_name}",
        )


def verify_csrf_contract(
    policy: dict[str, Any],
    repo_root: Path,
    defined_symbols: set[str],
) -> None:
    contracts = policy.get("nonOpenApiContracts")
    csrf = contracts.get("csrf") if isinstance(contracts, dict) else None
    require(isinstance(csrf, dict), "nonOpenApiContracts.csrf is missing")

    expected_endpoint = csrf.get("tokenEndpoint")
    expected_header = csrf.get("headerName")
    require(isinstance(expected_endpoint, str) and expected_endpoint.startswith("/api/"),
            "csrf.tokenEndpoint must be an /api/ path")
    require(isinstance(expected_header, str) and bool(expected_header),
            "csrf.headerName must be a non-empty string")

    controller = read_live_csharp_source(
        repo_root,
        "src/Coglatas.Web/Controllers/SecurityController.cs",
        defined_symbols,
    )
    options = read_live_csharp_source(
        repo_root,
        "src/Coglatas.Web/Configuration/SecurityOptions.cs",
        defined_symbols,
    )

    controller_route = re.search(r'\[Route\("([^"]+)"\)\]', controller)
    csrf_action = re.search(
        r'\[HttpGet\("([^"]+)"\)\]\s*'
        r'\[AllowAnonymous\]\s*'
        r'public\s+ActionResult<CsrfTokenResponse>\s+CsrfToken\s*\(',
        controller,
        flags=re.MULTILINE,
    )
    require(controller_route is not None and csrf_action is not None,
            "CSRF token controller route/action contract is missing")
    actual_endpoint = "/" + "/".join(
        part.strip("/")
        for part in (controller_route.group(1), csrf_action.group(1))
        if part.strip("/")
    )
    require(
        actual_endpoint == expected_endpoint,
        f"CSRF token endpoint drifted: expected {expected_endpoint!r}, got {actual_endpoint!r}",
    )

    header_match = re.search(
        r'CsrfHeaderName\s*=\s*"([^"]+)"\s*;',
        options,
    )
    require(header_match is not None, "SecurityOptions.CsrfHeaderName is missing")
    actual_header = header_match.group(1)
    require(
        actual_header == expected_header,
        f"CSRF header drifted: expected {expected_header!r}, got {actual_header!r}",
    )
    require(
        re.search(
            r'CsrfTokenResponse\s*\(\s*tokens\.RequestToken\s*\?\?\s*string\.Empty\s*,\s*'
            r'SecurityOptions\.CsrfHeaderName\s*\)',
            controller,
            flags=re.MULTILINE,
        )
        is not None,
        "CSRF token response must expose SecurityOptions.CsrfHeaderName",
    )


def verify_boundary(document: dict[str, Any], policy: dict[str, Any], repo_root: Path) -> None:
    defined_symbols = resolve_effective_csharp_symbols(policy, repo_root)
    verify_openapi_contract(document, policy)
    verify_signalr_contract(policy, repo_root, defined_symbols)
    verify_csrf_contract(policy, repo_root, defined_symbols)


def main() -> int:
    if len(sys.argv) != 2:
        print(
            "AV-MIG contract boundary verification failed: "
            "usage: verify_av_mig_contract_boundary.py <openapi.json>",
            file=sys.stderr,
        )
        return 2

    document_path = Path(sys.argv[1])
    try:
        document = json.loads(document_path.read_text(encoding="utf-8"))
        require(isinstance(document, dict), "OpenAPI document must be a JSON object")
        policy = load_policy(DEFAULT_POLICY)
        verify_boundary(document, policy, REPO_ROOT)
    except (OSError, UnicodeError, json.JSONDecodeError, BoundaryViolation) as exc:
        print(f"AV-MIG contract boundary verification failed: {exc}", file=sys.stderr)
        return 1

    print(
        "AV-MIG contract boundary verification passed: "
        "effective CookieAuth operation security, preprocessed live SignalR path/auth/signatures/events, "
        "and live CSRF endpoint/header are pinned"
    )
    return 0


if __name__ == "__main__":  # pragma: no cover - shared module is not a CLI entry point
    raise SystemExit(
        "use scripts/ci/verify_av_mig_contract_boundary.py; "
        "this module provides shared checks only"
    )
