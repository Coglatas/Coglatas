#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="${REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$repo_root"

fail() {
  printf 'SEC-04 contract test failed: %s\n' "$*" >&2
  exit 1
}

bash -n scripts/security/schemathesis-runner.sh
bash -n scripts/ci/generate-security-openapi-contract.sh
python3 -m py_compile \
  scripts/security/schemathesis_policy.py \
  scripts/security/schemathesis_hooks.py \
  scripts/security/process-schemathesis-report.py

python3 - <<'PY'
from pathlib import Path
import sys
sys.path.insert(0, str(Path("scripts/security").resolve()))
from schemathesis_policy import disclosure_reason

assert disclosure_reason(b'{"error":"System.InvalidOperationException"}') == "dotnet-exception"
assert disclosure_reason(b'Traceback (most recent call last):\n  File "x.py", line 1') == "python-traceback"
assert disclosure_reason(b'Npgsql.PostgresException SQLSTATE 23505') in {"dotnet-exception", "npgsql-internal", "sqlstate"}
assert disclosure_reason(b'{"message":"validation failed"}') is None
assert disclosure_reason(b'{"echo":"csrf-canary-123456"}', ["csrf-canary-123456"]) == "ephemeral-auth-material"
PY

runner="scripts/security/schemathesis-runner.sh"
config="scripts/security/schemathesis.toml"
generator="scripts/ci/generate-security-openapi-contract.sh"
grep -Fq 'schemathesis/schemathesis:4.25.2@sha256:' "$runner" || fail "Schemathesis image is not tag+digest pinned"
! grep -Eq 'schemathesis/schemathesis:(latest|stable)([^A-Za-z0-9_.-]|$)' "$runner" || fail "moving Schemathesis tag is forbidden"
grep -Fq -- '--request-retries 0' "$runner" || fail "network retries must remain disabled"
grep -Fq -- '--max-redirects 0' "$runner" || fail "redirect escape guard is missing"
grep -Fq -- '--output-sanitize true' "$runner" || fail "Schemathesis output sanitization must remain enabled"
grep -Fq -- '--workdir /tmp' "$runner" || fail "Schemathesis runtime metadata must stay inside the writable tmpfs"
grep -Fq -- '--config-file /work/scripts/security/schemathesis.toml' "$runner" || fail "Schemathesis policy config is not wired into the container"
grep -Fq "pr) printf '%s\\n' anonymous alpha-restricted alpha-owner ;;" "$runner" || fail "PR lane must include lower-privilege authenticated coverage"
grep -Fq 'negative_data_rejection.expected-statuses' "$config" || fail "negative-data rejection policy is missing"
grep -Eq '^[[:space:]]*415,' "$config" || fail "unsupported media type must count as a rejected invalid request"
grep -Fq 'security_scan_fetch_csrf' "$runner" || fail "SEC-03 CSRF harness is not reused"
grep -Fq 'no_sensitive_internal_error_disclosure' "$runner" || fail "custom disclosure check is not selected"
grep -Fq '_STRUCTURED_JSON_MEDIA_RANGE = "application/*+json"' scripts/security/schemathesis_hooks.py || fail "structured JSON media range is not normalized"
grep -Fq '_STRUCTURED_JSON_EXAMPLE = "application/vnd.coglatas+json"' scripts/security/schemathesis_hooks.py || fail "structured JSON media range lacks a concrete test subtype"
grep -Fq 'not_a_server_error' "$runner" || fail "unexpected 5xx check is not selected"
grep -Fq 'status_code_conformance' "$runner" || fail "status conformance check is not selected"
grep -Fq 'response_schema_conformance' "$runner" || fail "response schema conformance check is not selected"
grep -Eq '^[[:space:]]*operation_filters\+=\(--exclude-path[[:space:]]+/api/auth/login\)[[:space:]]*$' "$runner" || fail "authentication login lifecycle guard is missing"
! grep -Fq -- '--exclude-path /api/announcements' "$runner" || fail "announcement operations must remain in the fuzz scan"
grep -Fq -- '--exclude-path /api/auth/logout' "$runner" || fail "authenticated logout session guard is missing"
grep -Fq -- '--exclude-path /api/auth/change-password' "$runner" || fail "authenticated credential rotation guard is missing"
grep -Fq 'export ASPNETCORE_ENVIRONMENT=Test' "$generator" || fail "OpenAPI generator must force ASPNETCORE_ENVIRONMENT=Test"
grep -Fq 'export DOTNET_ENVIRONMENT=Test' "$generator" || fail "OpenAPI generator must force DOTNET_ENVIRONMENT=Test"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
secret='scanner-secret-123456789'
csrf='csrf-secret-987654321'
cookie='cookie-secret-123456789'
cat > "$tmp/auth.json" <<JSON
{"role":"alpha-owner","tenant":"security-alpha","headers":{"X-Tenant-Slug":"security-alpha","Cookie":"auth=$cookie","X-CSRF-Token":"$csrf"},"forbidden_values":["$cookie","$csrf","auth=$cookie"]}
JSON
seed=574143
cat > "$tmp/raw.ndjson" <<JSON
{"Initialize":{"command":"st run /work/openapi.json","schemathesis_version":"4.25.2","seed":$seed}}
{"ScenarioFinished":{"case":{"headers":{"Cookie":"auth=$cookie","X-CSRF-Token":"$csrf"},"body":{"password":"$secret","safe":"kept","content":"request-content-kept"}},"response":{"status_code":500,"headers":{},"content":"raw-response-should-not-persist","elapsed":0.1},"note":"$csrf"}}
JSON
cat > "$tmp/evidence.ndjson" <<JSON
{"event":"response","method":"GET","operation":"GET /api/auth/me","role":"alpha-owner","status":200}
JSON
COGLATAS_SECURITY_CI_PASSWORD="$secret" python3 scripts/security/process-schemathesis-report.py \
  --raw-report "$tmp/raw.ndjson" \
  --evidence "$tmp/evidence.ndjson" \
  --auth-file "$tmp/auth.json" \
  --output "$tmp/safe.ndjson" \
  --metadata "$tmp/meta.json" \
  --role alpha-owner \
  --lane pr \
  --seed "$seed" \
  --contract scripts/security/test-schemathesis-contract.sh \
  --scanner-exit 1

COGLATAS_SECURITY_CI_PASSWORD="$secret" python3 scripts/security/process-schemathesis-report.py \
  --raw-report "$tmp/raw.ndjson" \
  --evidence "$tmp/evidence.ndjson" \
  --auth-file "$tmp/auth.json" \
  --output "$tmp/safe-replay.ndjson" \
  --metadata "$tmp/meta-replay.json" \
  --role alpha-owner \
  --lane pr \
  --seed "$seed" \
  --contract scripts/security/test-schemathesis-contract.sh \
  --scanner-exit 1 >/dev/null
cmp --silent "$tmp/safe.ndjson" "$tmp/safe-replay.ndjson" || fail "same seed did not produce deterministic sanitized replay evidence"

for value in "$secret" "$csrf" "$cookie"; do
  ! grep -Fq "$value" "$tmp/safe.ndjson" || fail "sanitized report leaked a synthetic secret"
done
grep -Fq '"safe":"kept"' "$tmp/safe.ndjson" || fail "sanitizer removed non-sensitive minimized-case data"
grep -Fq '"seed": 574143' "$tmp/meta.json" || fail "metadata did not preserve replay seed"
! grep -Fq 'raw-response-should-not-persist' "$tmp/safe.ndjson" || fail "sanitizer persisted response content"
grep -Fq 'request-content-kept' "$tmp/safe.ndjson" || fail "sanitizer removed a request field named content"

cat > "$tmp/network-error.ndjson" <<JSON
{"event":"network_error","method":"GET","operation":"GET /api/auth/me","role":"alpha-owner"}
JSON
if COGLATAS_SECURITY_CI_PASSWORD="$secret" python3 scripts/security/process-schemathesis-report.py \
  --raw-report "$tmp/raw.ndjson" \
  --evidence "$tmp/network-error.ndjson" \
  --auth-file "$tmp/auth.json" \
  --output "$tmp/should-not-pass.ndjson" \
  --metadata "$tmp/should-not-pass.json" \
  --role alpha-owner \
  --lane pr \
  --seed "$seed" \
  --contract scripts/security/test-schemathesis-contract.sh \
  --scanner-exit 0 >/dev/null 2>&1; then
  fail "network failure was accepted as a successful scan"
fi

printf '%s\n' 'SEC-04 Schemathesis contract tests passed.'
