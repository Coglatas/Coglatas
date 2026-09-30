#!/usr/bin/env bash
set -Eeuo pipefail

: "${COGLATAS_SECURITY_CI_PASSWORD:?COGLATAS_SECURITY_CI_PASSWORD is required for the SEC-03/SEC-04/SEC-05/SEC-06/AUD-02 runtime gate}"

stage="${1:-all}"
project="${COGLATAS_SECURITY_CI_PROJECT:-coglatas-security-runtime-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}}"

if [[ -n "${COGLATAS_SECURITY_RUNTIME_STATE_DIR:-}" ]]; then
  runtime_state_dir="$COGLATAS_SECURITY_RUNTIME_STATE_DIR"
elif [[ "${GITHUB_ACTIONS:-}" == "true" && -n "${RUNNER_TEMP:-}" ]]; then
  runtime_state_dir="${RUNNER_TEMP}/coglatas-security-runtime-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}"
else
  runtime_state_dir="$(mktemp -d)"
fi

runtime_state_file="${runtime_state_dir}/runtime-state.env"
network="${project}_default"
zap_network="${project}_sec06_zap"
curl_image="curlimages/curl:8.21.0"
base_url="http://app:8080"
app_container=""
zap_network_owned=0

compose=(
  docker compose
  -p "$project"
  -f docker-compose.real-backend-smoke.yml
  -f docker-compose.security.yml
  -f docker-compose.security.runtime.yml
)

# shellcheck source=scripts/security/scanner-harness.sh
source scripts/security/scanner-harness.sh
# shellcheck source=scripts/security/authorization-negative-matrix.sh
source scripts/security/authorization-negative-matrix.sh
# shellcheck source=scripts/security/schemathesis-runner.sh
source scripts/security/schemathesis-runner.sh
# shellcheck source=scripts/security/zap-runner.sh
source scripts/security/zap-runner.sh
# shellcheck source=scripts/security/aud02-lifecycle.sh
source scripts/security/aud02-lifecycle.sh

fail() {
  printf 'Security runtime stage %s failed: %s\n' "$stage" "$*" >&2
  return 1
}

curl_network() {
  docker run --rm -i \
    --user 0:0 \
    --network "$network" \
    -v "$runtime_state_dir:/state" \
    "$curl_image" "$@"
}

security_scan_curl() {
  curl_network "$@"
}

# A hostname named "app" is not trusted by itself. The harness calls this guard
# before accepting that origin; it proves that the actual Compose app container
# is running and attached to the exact isolated network used by scanner curl and
# the SEC-04 Schemathesis container.
security_scan_transport_guard() {
  local target=$1 current_app_container
  [[ "$target" == "$base_url" ]] || return 1
  docker network inspect "$network" >/dev/null 2>&1 || return 1
  current_app_container="$("${compose[@]}" ps -q app)"
  [[ -n "$current_app_container" ]] || return 1
  docker inspect "$current_app_container" --format '{{json .NetworkSettings.Networks}}' |
    python3 -c '
import json
import sys
network = sys.argv[1]
networks = json.load(sys.stdin)
if network not in networks:
    raise SystemExit(f"app container is not attached to isolated scanner network {network!r}")
' "$network"
}

wait_ready() {
  local attempt
  for attempt in $(seq 1 90); do
    if curl_network \
      --fail --silent --show-error \
      -H "X-Tenant-Slug: security-alpha" \
      "$base_url/health/ready" \
      > "$runtime_state_dir/health-ready.json" 2>/dev/null; then
      grep -Fq '"status":"OK"' "$runtime_state_dir/health-ready.json" ||
        fail "readiness endpoint returned an unexpected payload"
      return 0
    fi
    sleep 2
  done
  fail "application did not become ready"
}

warm_up_sessions() {
  local role tenant jar
  for role in alpha-owner alpha-member alpha-restricted beta-owner; do
    tenant="$(security_scan_role_tenant "$role")" || return 1
    jar="$(security_scan_cookie_jar "$role")" || return 1
    security_scan_http \
      --fail --silent --show-error \
      -o /dev/null \
      -b "$jar" \
      -H "X-Tenant-Slug: $tenant" \
      "$SECURITY_SCAN_TARGET/api/auth/me" ||
      fail "authenticated warm-up failed for role '$role'" || return 1
  done
}

prepare_zap_network() {
  [[ -z "$app_container" ]] || fail "SEC-06 scanner network is already prepared" || return 1
  if docker network inspect "$zap_network" >/dev/null 2>&1; then
    fail "residual SEC-06 scanner network exists before setup"
    return 1
  fi
  app_container="$("${compose[@]}" ps -q app)"
  [[ -n "$app_container" ]] || fail "SEC-02 app container is unavailable for SEC-06" || return 1
  docker network create \
    --driver bridge \
    --internal \
    --label coglatas.security.control=SEC-06 \
    --label "coglatas.security.project=$project" \
    "$zap_network" >/dev/null
  zap_network_owned=1
  docker network connect --alias app "$zap_network" "$app_container"
}

release_zap_network() {
  [[ -n "$app_container" ]] || return 0
  docker network disconnect -f "$zap_network" "$app_container" >/dev/null 2>&1 || true
  docker network rm "$zap_network" >/dev/null 2>&1 || true
  zap_network_owned=0
  app_container=""
}

db_scalar() {
  local sql=$1
  "${compose[@]}" exec -T postgres \
    psql -v ON_ERROR_STOP=1 \
      -U coglatas_security \
      -d coglatas_security \
      -At -c "$sql"
}

assert_db_count() {
  local expected=$1
  local sql=$2
  local label=$3
  local actual
  actual="$(db_scalar "$sql" | tr -d '\r\n[:space:]')"
  [[ "$actual" == "$expected" ]] ||
    fail "$label expected $expected rows after restart, got '$actual'"
}

assert_fresh_start() {
  "${compose[@]}" down --volumes --remove-orphans
  if [[ -n "$("${compose[@]}" ps -aq)" ]]; then
    fail "fresh-start check found residual Compose containers"
    return 1
  fi
  if docker volume ls --quiet --filter "label=com.docker.compose.project=$project" | grep -q .; then
    fail "fresh-start check found residual Compose volumes"
    return 1
  fi
  if docker network ls --quiet --filter "label=com.docker.compose.project=$project" | grep -q .; then
    fail "fresh-start check found residual Compose networks"
    return 1
  fi
  if docker network inspect "$zap_network" >/dev/null 2>&1; then
    fail "fresh-start check found residual SEC-06 internal scanner network"
    return 1
  fi
}

persist_runtime_state() {
  local temp_file
  mkdir -p "$runtime_state_dir"
  umask 077
  temp_file="${runtime_state_file}.tmp"
  {
    printf 'AUD02_STAGE=%q\n' "${AUD02_STAGE:-init}"
    printf 'AUD02_FIXTURE_IDENTITY_BEFORE=%q\n' "${AUD02_FIXTURE_IDENTITY_BEFORE:-}"
    printf 'SECURITY_SCAN_STATE_DIR=%q\n' "${SECURITY_SCAN_STATE_DIR:-}"
    printf 'SECURITY_SCAN_HTTP_STATE_DIR=%q\n' "${SECURITY_SCAN_HTTP_STATE_DIR:-}"
    printf 'SECURITY_SCAN_STATE_OWNED=%q\n' "${SECURITY_SCAN_STATE_OWNED:-}"
    printf 'SECURITY_SCAN_TARGET=%q\n' "${SECURITY_SCAN_TARGET:-}"
  } > "$temp_file"
  mv "$temp_file" "$runtime_state_file"
}

load_runtime_state() {
  if [[ -f "$runtime_state_file" ]]; then
    # The file is generated only by persist_runtime_state(), contains no
    # credential material, and uses shell-escaped values.
    # shellcheck disable=SC1090
    source "$runtime_state_file"
  fi
  export AUD02_STAGE AUD02_FIXTURE_IDENTITY_BEFORE
  export SECURITY_SCAN_STATE_DIR SECURITY_SCAN_HTTP_STATE_DIR SECURITY_SCAN_STATE_OWNED SECURITY_SCAN_TARGET
}

require_scanner_state() {
  load_runtime_state
  [[ -n "${SECURITY_SCAN_STATE_DIR:-}" && -d "${SECURITY_SCAN_STATE_DIR:-}" ]] ||
    fail "SEC-03 scanner state is unavailable; the preparation/preflight stage did not complete"
  [[ "${SECURITY_SCAN_TARGET:-}" == "$base_url" ]] ||
    fail "SEC-03 scanner target state is missing or unexpected"
}

dump_failure_state() {
  echo "Security runtime failed; dumping redacted Compose state." >&2
  "${compose[@]}" ps 2>&1 | security_scan_redact_stream >&2 || true
  "${compose[@]}" logs --no-color postgres migrate app 2>&1 | security_scan_redact_stream >&2 || true
  if [[ -f artifacts/security/zap/preflight-stage.txt ]]; then
    grep -E '^stage=[a-z-]+$' artifacts/security/zap/preflight-stage.txt || true
  fi
}

cleanup_runtime() {
  load_runtime_state || true

  if docker network inspect "$zap_network" >/dev/null 2>&1; then
    local current_app_container
    current_app_container="$("${compose[@]}" ps -q app 2>/dev/null || true)"
    if [[ -n "$current_app_container" ]]; then
      docker network disconnect -f "$zap_network" "$current_app_container" >/dev/null 2>&1 || true
    fi
    docker network rm "$zap_network" >/dev/null 2>&1 || true
  fi

  security_scan_cleanup >/dev/null 2>&1 || true
  "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true

  rm -f -- "$runtime_state_file" "$runtime_state_dir/health-ready.json"
  rmdir -- "$runtime_state_dir" >/dev/null 2>&1 || true
}

stage_prepare() {
  mkdir -p "$runtime_state_dir"
  chmod 700 "$runtime_state_dir"
  [[ ! -e "$runtime_state_file" ]] ||
    fail "residual runtime state file exists before preparation: $runtime_state_file"

  "${compose[@]}" config --quiet
  "${compose[@]}" config --format json | python3 -c '
import json
import sys

document = json.load(sys.stdin)
app = document["services"]["app"]
if "build" in app:
    raise SystemExit("security runtime app must not retain the production Docker build")
if app.get("image") != "mcr.microsoft.com/dotnet/sdk:10.0.401":
    raise SystemExit("security runtime app must use the pinned .NET SDK image")
if app.get("ports"):
    raise SystemExit("security runtime app must not publish host ports")
environment = app.get("environment", {})
if str(environment.get("COGLATAS_SECURITY_CI_FIXTURE_ENABLED", "")).lower() != "true":
    raise SystemExit("security runtime fixture must remain enabled")
if str(environment.get("ASPNETCORE_ENVIRONMENT", "")).lower() != "test":
    raise SystemExit("security runtime app must remain Test-only")
'

  AUD02_STAGE=init
  AUD02_FIXTURE_IDENTITY_BEFORE=""
  unset SECURITY_SCAN_STATE_DIR SECURITY_SCAN_HTTP_STATE_DIR SECURITY_SCAN_STATE_OWNED SECURITY_SCAN_TARGET || true

  assert_fresh_start
  aud02_advance init fresh-start
  "${compose[@]}" up -d postgres migrate app
  wait_ready
  aud02_advance fresh-start ready
  persist_runtime_state

  echo "Security runtime prepared: isolated PostgreSQL/app stack is ready."
}

stage_sec03() {
  load_runtime_state
  [[ "${AUD02_STAGE:-}" == "ready" ]] ||
    fail "AUD-02 lifecycle must be at ready before SEC-03, got '${AUD02_STAGE:-<unset>}'"

  export ASPNETCORE_ENVIRONMENT=Test
  export COGLATAS_SECURITY_CI_FIXTURE_ENABLED=true
  export SECURITY_SCAN_TRANSPORT_KIND=compose
  export SECURITY_SCAN_STATE_PARENT="$runtime_state_dir"
  export SECURITY_SCAN_HTTP_STATE_PARENT="/state"

  unset SECURITY_SCAN_STATE_DIR SECURITY_SCAN_HTTP_STATE_DIR SECURITY_SCAN_STATE_OWNED SECURITY_SCAN_TARGET || true
  security_scan_init "$base_url"
  security_scan_preflight
  aud02_advance ready login
  warm_up_sessions
  aud02_advance login warm-up
  persist_runtime_state

  echo "SEC-03 scanner boundary and authenticated fixture verified."
}

stage_sec05() {
  require_scanner_state
  export ASPNETCORE_ENVIRONMENT=Test
  export COGLATAS_SECURITY_CI_FIXTURE_ENABLED=true
  export SECURITY_SCAN_TRANSPORT_KIND=compose

  security_authorization_negative_matrix_run

  # Claims/Evidence and Finding are implemented admin surfaces too. Both authorize
  # AuditView before protected artifact/finding lookup, so an ordinary Alpha member
  # must be rejected at the BFLA boundary even when supplied a syntactically valid
  # identifier. Re-render the same metadata-only evidence after appending the cases.
  sec05_case member-audit-claims-evidence audit-claims-evidence bfla-role-downgrade alpha-member \
    'security-alpha/member' 'security-alpha/admin-audit/claims-evidence' GET 'GET /api/admin/audit/claims-evidence' \
    "/api/admin/audit/claims-evidence?artifactVersionId=$SEC05_ALPHA_TASK_ID" \
    forbidden none __NO_BODY__ '' none
  sec05_case member-audit-findings audit-finding bfla-role-downgrade alpha-member \
    'security-alpha/member' 'security-alpha/admin-audit/findings' GET 'GET /api/admin/audit/findings' \
    "/api/admin/audit/findings?artifactVersionId=$SEC05_ALPHA_TASK_ID" \
    forbidden none __NO_BODY__ '' none
  sec05_write_evidence
  if (( SEC05_FAILURES != 0 )); then
    fail "$SEC05_FAILURES SEC-05 blocker case(s) failed after Audit Claim/Evidence/Finding coverage"
  fi

  echo "SEC-05 authorization negative matrix verified."
}

stage_sec04() {
  require_scanner_state
  export ASPNETCORE_ENVIRONMENT=Test
  export COGLATAS_SECURITY_CI_FIXTURE_ENABLED=true
  export SECURITY_SCAN_TRANSPORT_KIND=compose

  security_schemathesis_run_matrix "$network" "$runtime_state_dir"
}

stage_sec06() {
  require_scanner_state
  export ASPNETCORE_ENVIRONMENT=Test
  export COGLATAS_SECURITY_CI_FIXTURE_ENABLED=true
  export SECURITY_SCAN_TRANSPORT_KIND=compose

  # Keep errexit semantics inside the ZAP runner while guaranteeing that its
  # temporary internal network is released even when the scanner stage fails.
  (
    app_container=""
    zap_network_owned=0
    prepare_zap_network
    trap 'release_zap_network' EXIT
    security_zap_run_matrix "$zap_network" "$SECURITY_SCAN_STATE_DIR" "$app_container"
    release_zap_network
    trap - EXIT
  )
}

stage_aud02() {
  require_scanner_state
  [[ "${AUD02_STAGE:-}" == "warm-up" ]] ||
    fail "AUD-02 lifecycle must be at warm-up before restart verification, got '${AUD02_STAGE:-<unset>}'"

  export ASPNETCORE_ENVIRONMENT=Test
  export COGLATAS_SECURITY_CI_FIXTURE_ENABLED=true
  export SECURITY_SCAN_TRANSPORT_KIND=compose

  aud02_capture_fixture_evidence
  security_scan_teardown
  aud02_advance fixture-evidence teardown

  # A process restart forces both Test-only seed layers to seed the same real
  # PostgreSQL database a second time. Readiness, exact fixture identity, and exact
  # canary row counts jointly prove idempotence without exposing raw fixture IDs.
  "${compose[@]}" restart app
  wait_ready
  aud02_verify_restart_identity

  assert_db_count 2 \
    "SELECT COUNT(*) FROM tenants WHERE \"Slug\" IN ('security-alpha','security-beta');" \
    "tenant canaries"
  assert_db_count 4 \
    "SELECT COUNT(*) FROM users WHERE lower(\"Email\") IN ('security-alpha-owner@example.test','security-alpha-member@example.test','security-alpha-restricted@example.test','security-beta-owner@example.test');" \
    "synthetic identities"
  assert_db_count 2 \
    "SELECT COUNT(*) FROM workspaces WHERE \"Slug\" IN ('sec02-alpha-workspace','sec02-beta-workspace');" \
    "workspace canaries"
  assert_db_count 2 \
    "SELECT COUNT(*) FROM projects WHERE \"Slug\" IN ('sec02-alpha-project','sec02-beta-project');" \
    "project canaries"
  assert_db_count 1 \
    "SELECT COUNT(*) FROM conversations WHERE \"Title\"='SEC05 ALPHA SHADOW CONVERSATION CANARY';" \
    "SEC-05 same-tenant shadow conversation"
  assert_db_count 2 \
    "SELECT COUNT(*) FROM notifications WHERE \"LogicalKey\" IN ('sec05-alpha-task-open-canary','sec05-beta-task-open-canary') AND \"DeletedAt\" IS NULL;" \
    "SEC-05 notification canaries"
  assert_db_count 2 \
    "SELECT COUNT(*) FROM announcements WHERE \"Title\" IN ('SEC05 ALPHA ANNOUNCEMENT CANARY','SEC05 BETA ANNOUNCEMENT CANARY') AND \"DeletedAt\" IS NULL;" \
    "SEC-05 announcement canaries"

  aud02_write_summary
  persist_runtime_state
  echo "AUD-02 restart identity and fixture idempotence verified."
}

stage_cleanup() {
  if [[ "${COGLATAS_SECURITY_RUNTIME_FAILED:-false}" == "true" ]]; then
    dump_failure_state
  fi
  cleanup_runtime
  echo "Security runtime cleanup completed."
}

run_all() {
  local status=0
  trap 'status=$?; if (( status != 0 )); then dump_failure_state; fi; cleanup_runtime; exit "$status"' EXIT

  stage_prepare
  stage_sec03
  stage_sec05
  stage_sec04
  stage_sec06
  stage_aud02

  trap - EXIT
  cleanup_runtime
  echo "SEC-03 scanner boundary, SEC-05 authorization negative matrix, SEC-04 Schemathesis contract fuzzing, SEC-06 authenticated ZAP API DAST, and AUD-02 restart identity verified on disposable PostgreSQL."
}

case "$stage" in
  prepare) stage_prepare ;;
  sec03) stage_sec03 ;;
  sec05) stage_sec05 ;;
  sec04) stage_sec04 ;;
  sec06) stage_sec06 ;;
  aud02) stage_aud02 ;;
  cleanup) stage_cleanup ;;
  all) run_all ;;
  *)
    printf 'Unknown security runtime stage: %s\n' "$stage" >&2
    printf 'Expected one of: prepare, sec03, sec05, sec04, sec06, aud02, cleanup, all\n' >&2
    exit 2
    ;;
esac
