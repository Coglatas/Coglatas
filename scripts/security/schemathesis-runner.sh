#!/usr/bin/env bash
# SEC-04 Schemathesis runner. Source from the SEC-03 runtime smoke after
# security_scan_preflight so it can reuse the exact ephemeral identities and
# transport-bound scanner network.

SCHEMATHESIS_VERSION="4.25.2"
SCHEMATHESIS_IMAGE="schemathesis/schemathesis:4.25.2@sha256:72d6907a936f7b5f08f137c8f84c89eb3ab7834956d9af416e7b6510ebe4e065"
SCHEMATHESIS_CONTRACT="artifacts/openapi/coglatas-openapi.json"
SCHEMATHESIS_CHECKS="not_a_server_error,status_code_conformance,content_type_conformance,response_headers_conformance,response_schema_conformance,negative_data_rejection,positive_data_acceptance,missing_required_header,unsupported_method,allow_header_conformance,no_sensitive_internal_error_disclosure"

security_schemathesis_fail() {
  printf 'SEC-04 Schemathesis failed: %s\n' "$*" >&2
  return 1
}

security_schemathesis_require_contract() {
  [[ -f "$SCHEMATHESIS_CONTRACT" ]] || security_schemathesis_fail "authoritative SEC-01 OpenAPI artifact is missing" || return 1
  python3 scripts/ci/verify-openapi.py "$SCHEMATHESIS_CONTRACT" ||
    security_schemathesis_fail "authoritative SEC-01 OpenAPI artifact failed re-verification" || return 1
}

security_schemathesis_lane() {
  local requested="${COGLATAS_SECURITY_SCHEMATHESIS_LANE:-}"
  if [[ -z "$requested" ]]; then
    if [[ "${GITHUB_EVENT_NAME:-}" == "pull_request" ]]; then
      requested="pr"
    elif [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
      requested="deep"
    else
      requested="pr"
    fi
  fi
  case "$requested" in
    pr|deep) printf '%s\n' "$requested" ;;
    *) security_schemathesis_fail "COGLATAS_SECURITY_SCHEMATHESIS_LANE must be pr or deep"; return 1 ;;
  esac
}

security_schemathesis_default_seed() {
  local lane=$1
  python3 - "$lane" <<'PY'
from __future__ import annotations

import hashlib
import os
import sys

lane = sys.argv[1]
sha = os.environ.get("GITHUB_SHA", "local-sec04")
if lane == "deep":
    material = f"{sha}:{os.environ.get('GITHUB_RUN_ID', '0')}:{os.environ.get('GITHUB_RUN_ATTEMPT', '0')}"
else:
    material = sha
value = int(hashlib.sha256(material.encode("utf-8")).hexdigest()[:8], 16) % 2147483647
print(value or 574042)
PY
}

security_schemathesis_roles() {
  case "$1" in
    # PR coverage must include a lower-privilege authenticated principal.
    # Otherwise tenant-admin status/authorization regressions are only discovered
    # by the post-merge deep lane.
    pr) printf '%s\n' anonymous alpha-restricted alpha-owner ;;
    # Lower-privilege principals run before owners so expected fuzz mutations are
    # less likely to affect later coverage. Stateful exploration is limited to
    # alpha-restricted, where the synthetic fixture cannot administer the canary
    # Workspace/Project; this is the mutation-safe stateful lane for SEC-04.
    deep) printf '%s\n' anonymous alpha-restricted alpha-member alpha-owner beta-owner ;;
    *) security_schemathesis_fail "unknown lane '$1'"; return 1 ;;
  esac
}

security_schemathesis_role_tenant() {
  case "$1" in
    anonymous|alpha-owner|alpha-member|alpha-restricted) printf '%s\n' security-alpha ;;
    beta-owner) printf '%s\n' security-beta ;;
    *) security_schemathesis_fail "unknown scanner role '$1'"; return 1 ;;
  esac
}

security_schemathesis_role_seed() {
  local base=$1 role=$2 offset
  case "$role" in
    anonymous) offset=0 ;;
    alpha-owner) offset=101 ;;
    alpha-member) offset=211 ;;
    alpha-restricted) offset=307 ;;
    beta-owner) offset=401 ;;
    *) return 1 ;;
  esac
  python3 - "$base" "$offset" <<'PY'
import sys
base, offset = map(int, sys.argv[1:])
# Hypothesis accepts ordinary signed integers. Keep role-derived seeds stable and
# well inside 32-bit range for simple manual replay.
print((base + offset) % 2147483647 or 1)
PY
}

security_schemathesis_prepare_auth_file() {
  local role=$1 output_host=$2 tenant csrf_header="" csrf_token="" cookie_jar=""
  tenant="$(security_schemathesis_role_tenant "$role")" || return 1

  if [[ "$role" != "anonymous" ]]; then
    security_scan_fetch_csrf "$role" || return 1
    csrf_header="$SECURITY_SCAN_CSRF_HEADER_NAME"
    csrf_token="$SECURITY_SCAN_CSRF_TOKEN"
    cookie_jar="$(security_scan_host_path "${role}.cookies")"
    [[ -s "$cookie_jar" ]] || security_schemathesis_fail "SEC-03 cookie jar is missing for '$role'" || return 1
  fi

  SECURITY_SCHEMATHESIS_TENANT="$tenant" \
  SECURITY_SCHEMATHESIS_CSRF_HEADER="$csrf_header" \
  SECURITY_SCHEMATHESIS_CSRF_TOKEN="$csrf_token" \
  SECURITY_SCHEMATHESIS_COOKIE_JAR="$cookie_jar" \
  SECURITY_SCHEMATHESIS_ROLE="$role" \
  SECURITY_SCHEMATHESIS_FIXTURE_CREDENTIAL="${COGLATAS_SECURITY_CI_PASSWORD:-}" \
    python3 - "$output_host" <<'PY'
from __future__ import annotations

import json
import os
import stat
import sys
from pathlib import Path

output = Path(sys.argv[1])
role = os.environ["SECURITY_SCHEMATHESIS_ROLE"]
tenant = os.environ["SECURITY_SCHEMATHESIS_TENANT"]
csrf_header = os.environ.get("SECURITY_SCHEMATHESIS_CSRF_HEADER", "")
csrf_token = os.environ.get("SECURITY_SCHEMATHESIS_CSRF_TOKEN", "")
jar_path = os.environ.get("SECURITY_SCHEMATHESIS_COOKIE_JAR", "")
headers = {"X-Tenant-Slug": tenant}
forbidden: list[str] = []
fixture_credential = os.environ.get("SECURITY_SCHEMATHESIS_FIXTURE_CREDENTIAL", "")
if fixture_credential:
    forbidden.append(fixture_credential)

if role != "anonymous":
    cookies: list[tuple[str, str]] = []
    for raw in Path(jar_path).read_text(encoding="utf-8").splitlines():
        if raw.startswith("#HttpOnly_"):
            raw = raw[len("#HttpOnly_") :]
        elif raw.startswith("#") or not raw:
            continue
        parts = raw.split("\t")
        if len(parts) < 7:
            raise SystemExit("invalid SEC-03 Netscape cookie jar")
        name, value = parts[5], parts[6]
        if name and value:
            cookies.append((name, value))
            forbidden.append(value)
    if not cookies:
        raise SystemExit(f"no cookies found for SEC-04 role {role}")
    cookie_header = "; ".join(f"{name}={value}" for name, value in cookies)
    headers["Cookie"] = cookie_header
    headers[csrf_header] = csrf_token
    forbidden.extend((cookie_header, csrf_token))

payload = {
    "role": role,
    "tenant": tenant,
    "headers": headers,
    "forbidden_values": sorted(set(forbidden)),
}
output.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
output.chmod(stat.S_IRUSR | stat.S_IWUSR)
PY
}

security_schemathesis_run_role() {
  local lane=$1 role=$2 base_seed=$3 network=$4 mount_root=$5
  local seed examples phases auth_host auth_http evidence_host evidence_http raw_host raw_http
  local artifact_dir safe_report metadata status process_status
  local -a operation_filters=()

  seed="$(security_schemathesis_role_seed "$base_seed" "$role")" || return 1
  auth_host="$(security_scan_host_path "schemathesis-${role}-auth.json")"
  auth_http="${SECURITY_SCAN_HTTP_STATE_DIR}/schemathesis-${role}-auth.json"
  evidence_host="$(security_scan_host_path "schemathesis-${role}-evidence.ndjson")"
  evidence_http="${SECURITY_SCAN_HTTP_STATE_DIR}/schemathesis-${role}-evidence.ndjson"
  raw_host="$(security_scan_host_path "schemathesis-${role}-raw.ndjson")"
  raw_http="${SECURITY_SCAN_HTTP_STATE_DIR}/schemathesis-${role}-raw.ndjson"
  artifact_dir="artifacts/security/schemathesis"
  safe_report="$artifact_dir/${role}.ndjson"
  metadata="$artifact_dir/${role}.metadata.json"

  printf 'SECURITY_SCAN_PROGRESS role_auth_prepare_start role=%s\n' "$role"
  security_schemathesis_prepare_auth_file "$role" "$auth_host" || return 1
  printf 'SECURITY_SCAN_PROGRESS role_auth_prepare_ok role=%s\n' "$role"
  : > "$evidence_host"
  chmod 600 "$evidence_host"
  rm -f "$raw_host" "$safe_report" "$metadata"
  mkdir -p "$artifact_dir"

  # SEC-03 owns authentication lifecycle verification. Excluding login from the
  # high-volume API fuzz lanes prevents shared auth/rate-limit state from being
  # mutated between principals while preserving dedicated login negative tests.
  operation_filters+=(--exclude-path /api/auth/login)
  if [[ "$role" != "anonymous" ]]; then
    operation_filters+=(--exclude-path /api/auth/logout)
    operation_filters+=(--exclude-path /api/auth/change-password)
  fi

  if [[ "$lane" == "pr" ]]; then
    examples="${COGLATAS_SECURITY_SCHEMATHESIS_PR_EXAMPLES:-3}"
    phases="examples,coverage,fuzzing"
  else
    examples="${COGLATAS_SECURITY_SCHEMATHESIS_DEEP_EXAMPLES:-20}"
    if [[ "$role" == "alpha-restricted" ]]; then
      phases="examples,coverage,fuzzing,stateful"
    else
      phases="examples,coverage,fuzzing"
    fi
  fi

  printf 'SECURITY_SCAN_PROGRESS role_scan_start role=%s\n' "$role"
  printf 'SEC-04 Schemathesis: lane=%s role=%s seed=%s phases=%s max-examples=%s\n' \
    "$lane" "$role" "$seed" "$phases" "$examples"

  set +e
  docker run --rm \
    --user "$(id -u):$(id -g)" \
    --network "$network" \
    --read-only \
    --cap-drop ALL \
    --security-opt no-new-privileges \
    --tmpfs /tmp:rw,nosuid,nodev,noexec,size=64m \
    --workdir /tmp \
    -e HOME=/tmp \
    -e SCHEMATHESIS_HOOKS=/work/scripts/security/schemathesis_hooks.py \
    -e COGLATAS_SECURITY_SCHEMATHESIS_AUTH_FILE="$auth_http" \
    -e COGLATAS_SECURITY_SCHEMATHESIS_EVIDENCE_FILE="$evidence_http" \
    -e COGLATAS_SECURITY_SCHEMATHESIS_ROLE="$role" \
    -v "$PWD:/work:ro" \
    -v "$mount_root:/state" \
    "$SCHEMATHESIS_IMAGE" \
      --config-file /work/scripts/security/schemathesis.toml \
      run "/work/$SCHEMATHESIS_CONTRACT" \
      --url "$SECURITY_SCAN_TARGET" \
      --phases "$phases" \
      --checks "$SCHEMATHESIS_CHECKS" \
      "${operation_filters[@]}" \
      --max-examples "$examples" \
      --seed "$seed" \
      --workers 1 \
      --max-failures 20 \
      --request-timeout 10 \
      --request-retries 0 \
      --max-redirects 0 \
      --generation-database none \
      --generation-with-security-parameters false \
      --output-sanitize true \
      --output-truncate false \
      --report-ndjson-path "$raw_http" \
      --warnings off \
      --no-color \
      2>&1 | security_scan_redact_stream
  status=${PIPESTATUS[0]}
  set -e

  set +e
  python3 scripts/security/process-schemathesis-report.py \
    --raw-report "$raw_host" \
    --evidence "$evidence_host" \
    --auth-file "$auth_host" \
    --output "$safe_report" \
    --metadata "$metadata" \
    --role "$role" \
    --lane "$lane" \
    --seed "$seed" \
    --contract "$SCHEMATHESIS_CONTRACT" \
    --scanner-exit "$status"
  process_status=$?
  set -e
  (( process_status == 0 )) || return "$process_status"

  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      printf '### SEC-04 Schemathesis — %s\n\n' "$role"
      printf -- '- Lane: `%s`\n' "$lane"
      printf -- '- Seed: `%s`\n' "$seed"
      printf -- '- Tool: `schemathesis/%s` (digest pinned)\n' "$SCHEMATHESIS_VERSION"
      printf -- '- Contract: `%s`\n' "$(sha256sum "$SCHEMATHESIS_CONTRACT" | awk '{print $1}')"
      printf -- '- Sanitized report: `%s`\n' "$safe_report"
      printf -- '- Replay: after `dotnet restore Coglatas.slnx`, run `bash scripts/ci/generate-security-openapi-contract.sh`, then set a fresh `COGLATAS_SECURITY_CI_PASSWORD` and run `COGLATAS_SECURITY_SCHEMATHESIS_LANE=%s COGLATAS_SECURITY_SCHEMATHESIS_SEED=%s bash scripts/ci/run-security-runtime-smoke.sh`\n\n' "$lane" "$base_seed"
    } >> "$GITHUB_STEP_SUMMARY"
  fi

  (( status == 0 )) || security_schemathesis_fail "role '$role' found a blocking contract/property failure (seed $seed)" || return 1
  printf 'SECURITY_SCAN_PROGRESS role_scan_complete role=%s\n' "$role"
}

security_schemathesis_wait_healthy() {
  local role=$1 attempt
  printf 'SECURITY_SCAN_PROGRESS post_role_readiness_start role=%s\n' "$role"
  for attempt in 1 2 3 4 5; do
    if security_scan_health; then
      printf 'SECURITY_SCAN_PROGRESS post_role_readiness_ok role=%s\n' "$role"
      return 0
    fi
    if (( attempt < 5 )); then
      sleep 2
    fi
  done
  printf 'SECURITY_SCAN_PROGRESS post_role_readiness_failed role=%s\n' "$role"
  security_schemathesis_fail "application remained unhealthy after role '$role'"
}

security_schemathesis_run_matrix() {
  local network=$1 mount_root=$2 lane base_seed role version
  security_scan_require_no_xtrace || return 1
  security_schemathesis_require_contract || return 1
  security_scan_require_transport_binding "$SECURITY_SCAN_TARGET" || return 1

  rm -rf artifacts/security/schemathesis
  mkdir -p artifacts/security/schemathesis

  lane="$(security_schemathesis_lane)" || return 1
  base_seed="${COGLATAS_SECURITY_SCHEMATHESIS_SEED:-$(security_schemathesis_default_seed "$lane")}"
  [[ "$base_seed" =~ ^[0-9]+$ ]] || security_schemathesis_fail "COGLATAS_SECURITY_SCHEMATHESIS_SEED must be an integer" || return 1

  docker pull "$SCHEMATHESIS_IMAGE" >/dev/null
  version="$(docker run --rm "$SCHEMATHESIS_IMAGE" --version | tr -d '\r')"
  [[ "$version" == *"$SCHEMATHESIS_VERSION"* ]] ||
    security_schemathesis_fail "pinned image did not report Schemathesis $SCHEMATHESIS_VERSION" || return 1

  # Keep the role list on a dedicated descriptor. SEC-03 uses `docker run -i`
  # for HTTP probes, and inheriting the loop on stdin lets those probes consume
  # the next role name before the shell can read it.
  while IFS= read -r role <&3; do
    [[ -n "$role" ]] || continue
    security_schemathesis_run_role "$lane" "$role" "$base_seed" "$network" "$mount_root" || return 1
    security_schemathesis_wait_healthy "$role" || return 1
  done 3< <(security_schemathesis_roles "$lane")

  printf 'SEC-04 Schemathesis contract fuzzing passed: lane=%s roles=%s\n' \
    "$lane" "$(security_schemathesis_roles "$lane" | paste -sd, -)"
}
