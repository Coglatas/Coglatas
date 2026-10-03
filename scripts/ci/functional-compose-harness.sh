#!/usr/bin/env bash
set -Eeuo pipefail

DEFAULT_COMPOSE_FILE="infra/compose/test/real-backend-smoke.yml"
DEFAULT_DIAGNOSTIC_DIR="test-results"
DEFAULT_SETUP_TIMEOUT_SECONDS="240"
DEFAULT_ACTOR_EMAIL="e2e-user@example.test"
DEFAULT_ACTOR_PASSWORD="E2eSmoke!23456"

compose=()
cleanup_done=0
last_wait_state="not created"

sanitize_project_name() {
  local value
  value="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9_-]+/-/g; s/^[^a-z0-9]+//; s/[-_]+$//')"
  value="${value:0:63}"
  value="$(printf '%s' "$value" | sed -E 's/[-_]+$//')"
  if [[ -z "$value" ]]; then
    value="coglatas"
  fi
  printf '%s\n' "$value"
}

build_project_name() {
  local override raw
  override="${FUNCTIONAL_COMPOSE_PROJECT_NAME:-${REAL_BACKEND_SMOKE_COMPOSE_PROJECT_NAME:-}}"
  if [[ -n "$override" ]]; then
    sanitize_project_name "$override"
    return
  fi

  raw="coglatas-functional-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-${BASHPID}"
  sanitize_project_name "$raw"
}

validate_fixture_profile() {
  local email password
  email="${COGLATAS_BROWSER_SMOKE_EMAIL:-$DEFAULT_ACTOR_EMAIL}"
  password="${COGLATAS_BROWSER_SMOKE_PASSWORD:-$DEFAULT_ACTOR_PASSWORD}"

  case "${email,,}" in
    *@example.test) ;;
    *) return 1 ;;
  esac
  [[ -n "$password" ]] || return 1

  export COGLATAS_BROWSER_SMOKE_EMAIL="$email"
  export COGLATAS_BROWSER_SMOKE_PASSWORD="$password"
  export COGLATAS_BROWSER_SMOKE_SEED_ENABLED="true"
  export COGLATAS_BROWSER_SMOKE_RESPONSE_GATE_ENABLED="true"
}

sanitize_file() {
  local source_file="$1"
  local target_file="$2"
  python3 - "$source_file" "$target_file" <<'PY'
import os
import re
import sys
from pathlib import Path

source = Path(sys.argv[1])
target = Path(sys.argv[2])
text = source.read_text(encoding="utf-8", errors="replace")
patterns = (
    (r"(?i)(POSTGRES_PASSWORD\s*[:=]\s*)[^\r\n]+", r"\1[redacted]"),
    (r"(?i)(SYNCFUSION_LICENSE\s*[:=]\s*)[^\r\n]+", r"\1[redacted]"),
    (r"(?i)(COGLATAS_[A-Z0-9_]*(?:PASSWORD|TOKEN|SECRET|LICENSE)\s*[:=]\s*)[^\r\n]+", r"\1[redacted]"),
    (r"(?i)(\b[A-Z0-9_-]*(?:TOKEN|SECRET|LICENSE)\s*[:=]\s*)[^\r\n]+", r"\1[redacted]"),
    (r"(?i)((?:Password|Pwd)=)[^;\s\r\n]+", r"\1[redacted]"),
    (r"(?i)(Authorization\s*:\s*)[^\r\n]+", r"\1[redacted]"),
    (r"(?i)((?:Cookie|Set-Cookie)\s*:\s*)[^\r\n]+", r"\1[redacted]"),
    (r"(?i)((?:X-CSRF-Token|CSRF(?:Token)?)\s*[:=]\s*)[^\r\n]+", r"\1[redacted]"),
    (r"(?i)((?:Invite|Invitation)[A-Za-z-]*Token\s*[:=]\s*)[^\r\n]+", r"\1[redacted]"),
    (r'(?i)("(?:password|token|secret|license|authorization|cookie|csrfToken|inviteToken|invitationToken)"\s*:\s*")[^"]*(")', r"\1[redacted]\2"),
)
for pattern, replacement in patterns:
    text = re.sub(pattern, replacement, text)
for name in ("SYNCFUSION_LICENSE", "COGLATAS_BROWSER_SMOKE_PASSWORD", "POSTGRES_PASSWORD"):
    secret = os.environ.get(name, "")
    if len(secret) >= 4:
        text = text.replace(secret, "[redacted]")
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(text, encoding="utf-8")
PY
}

select_compose() {
  if docker compose version >/dev/null 2>&1; then
    compose=(docker compose)
    return 0
  fi
  if command -v docker-compose >/dev/null 2>&1 && docker-compose version >/dev/null 2>&1; then
    compose=(docker-compose)
    return 0
  fi
  return 1
}

append_compose_scope() {
  local project_name="$1"
  local files_value="$2"
  local file
  local -a files
  IFS=',' read -r -a files <<< "$files_value"
  compose+=( -p "$project_name" )
  for file in "${files[@]}"; do
    [[ -n "$file" ]] || continue
    compose+=( -f "$file" )
  done
}

get_service_container_id() {
  local service="$1"
  "${compose[@]}" ps --all -q "$service" 2>/dev/null | head -n 1
}

inspect_container_state() {
  local container_id="$1"
  docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{.State.ExitCode}}' "$container_id" 2>/dev/null || true
}

wait_for_healthy() {
  local service="$1"
  local timeout_seconds="$2"
  local deadline container_id state status health exit_code
  deadline=$((SECONDS + timeout_seconds))
  last_wait_state="not created"

  while (( SECONDS < deadline )); do
    container_id="$(get_service_container_id "$service")"
    if [[ -n "$container_id" ]]; then
      state="$(inspect_container_state "$container_id")"
      [[ -n "$state" ]] && last_wait_state="$state"
      read -r status health exit_code <<< "$last_wait_state"
      if [[ "$status" == "running" && "$health" == "healthy" ]]; then
        return 0
      fi
      if [[ "$status" == "exited" || "$status" == "dead" ]]; then
        return 1
      fi
      if [[ "$exit_code" =~ ^[0-9]+$ ]] && (( exit_code != 0 )); then
        return 1
      fi
    fi
    sleep 1
  done
  return 1
}

wait_for_completed() {
  local service="$1"
  local timeout_seconds="$2"
  local deadline container_id state status health exit_code
  deadline=$((SECONDS + timeout_seconds))
  last_wait_state="not created"

  while (( SECONDS < deadline )); do
    container_id="$(get_service_container_id "$service")"
    if [[ -n "$container_id" ]]; then
      state="$(inspect_container_state "$container_id")"
      [[ -n "$state" ]] && last_wait_state="$state"
      read -r status health exit_code <<< "$last_wait_state"
      if [[ "$status" == "exited" && "$exit_code" == "0" ]]; then
        return 0
      fi
      if [[ "$status" == "exited" || "$status" == "dead" ]]; then
        return 1
      fi
    fi
    sleep 1
  done
  return 1
}

collect_diagnostics() {
  local diagnostic_dir="${FUNCTIONAL_DIAGNOSTIC_DIR:-$DEFAULT_DIAGNOSTIC_DIR}"
  local temporary migration_id migration_state service safe_service
  local -a services=(postgres migrate app real-backend-playwright)
  [[ ${#compose[@]} -gt 0 ]] || return 0

  mkdir -p "$diagnostic_dir"
  temporary="$(mktemp)"
  "${compose[@]}" ps --all >"$temporary" 2>&1 || true
  sanitize_file "$temporary" "$diagnostic_dir/functional-compose-ps.txt"

  migration_id="$(get_service_container_id migrate)"
  migration_state="not created"
  if [[ -n "$migration_id" ]]; then
    migration_state="$(inspect_container_state "$migration_id")"
  fi
  printf '%s\n' "$migration_state" >"$temporary"
  sanitize_file "$temporary" "$diagnostic_dir/functional-migration-status.txt"

  for service in "${services[@]}"; do
    safe_service="${service//[^a-zA-Z0-9_-]/-}"
    "${compose[@]}" logs --no-color --tail 300 "$service" >"$temporary" 2>&1 || true
    sanitize_file "$temporary" "$diagnostic_dir/functional-${safe_service}.log"
  done
  rm -f "$temporary"
}

cleanup() {
  if (( cleanup_done != 0 )); then
    return 0
  fi
  cleanup_done=1
  if [[ ${#compose[@]} -gt 0 ]]; then
    if ! "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1; then
      printf '[INFRA/SETUP FAILURE] phase=cleanup: isolated Functional volumes could not be removed.\n' >&2
      return 1
    fi
  fi
}

setup_failure() {
  local phase="$1"
  local message="$2"
  printf '[INFRA/SETUP FAILURE] phase=%s: %s\n' "$phase" "$message" >&2
  collect_diagnostics || true
  return 1
}

run_harness() {
  local project_name compose_files timeout_seconds suite_status setup_started
  local -a suite_args=(run --rm)

  if ! validate_fixture_profile; then
    printf '[INFRA/SETUP FAILURE] phase=fixture-profile: canonical actor must be a synthetic @example.test identity with a non-empty password.\n' >&2
    return 1
  fi
  if ! select_compose; then
    printf '[INFRA/SETUP FAILURE] phase=validate-host: Docker Compose is required for Functional CI.\n' >&2
    return 1
  fi

  project_name="$(build_project_name)"
  compose_files="${FUNCTIONAL_COMPOSE_FILES:-$DEFAULT_COMPOSE_FILE}"
  timeout_seconds="${FUNCTIONAL_SETUP_TIMEOUT_SECONDS:-$DEFAULT_SETUP_TIMEOUT_SECONDS}"
  append_compose_scope "$project_name" "$compose_files"
  export COMPOSE_PROJECT_NAME="$project_name"

  trap 'exit 130' INT
  trap 'exit 143' TERM
  trap 'status=$?; trap - EXIT; if ! cleanup; then (( status != 0 )) || status=1; fi; exit "$status"' EXIT

  setup_started=$SECONDS
  docker info >/dev/null 2>&1 || setup_failure validate-host "Docker daemon is unavailable."
  "${compose[@]}" config --quiet || setup_failure validate-compose-config "Compose configuration is invalid."
  if [[ "${COGLATAS_REUSE_PREBUILT_APP_IMAGE:-}" != "1" ]]; then
    "${compose[@]}" build app || setup_failure build-images "Required Functional application image failed to build."
  fi
  "${compose[@]}" up --detach postgres || setup_failure start-postgres "PostgreSQL failed to start."
  wait_for_healthy postgres "$timeout_seconds" || setup_failure postgres-readiness "PostgreSQL did not become healthy: $last_wait_state"
  "${compose[@]}" up --detach migrate || setup_failure apply-migrations "Migration service failed to start."
  wait_for_completed migrate "$timeout_seconds" || setup_failure migration-head "Migration head did not complete successfully: $last_wait_state"
  "${compose[@]}" up --detach --no-deps app || setup_failure start-application "Application failed to start."
  wait_for_healthy app "$timeout_seconds" || setup_failure application-readiness "Application did not become healthy: $last_wait_state"

  if [[ -n "${COGLATAS_FUNCTIONAL_DOMAIN:-}" ]]; then
    mkdir -p artifacts/functional
    printf '{"setupSeconds":%s}\n' "$((SECONDS - setup_started))" > "artifacts/functional/setup-${COGLATAS_FUNCTIONAL_DOMAIN}.json"
  fi

  if [[ "${COGLATAS_REAL_BACKEND_P0_SETUP:-}" == "1" ]]; then
    suite_args+=(--env COGLATAS_REAL_BACKEND_P0_SETUP=1)
  fi
  suite_args+=(real-backend-playwright)

  set +e
  "${compose[@]}" "${suite_args[@]}"
  suite_status=$?
  set -e
  if (( suite_status != 0 )); then
    printf '[PRODUCT TEST FAILURE] phase=execute-suite: suite process exited with code %s\n' "$suite_status" >&2
    collect_diagnostics || true
    return "$suite_status"
  fi

  printf 'Functional Compose harness completed successfully for isolated project %s.\n' "$project_name"
}

self_test() {
  local name temporary_input temporary_output secret
  name="$(sanitize_project_name 'FCI / Lane #1')"
  [[ "$name" == "fci-lane-1" ]]
  [[ "$name" =~ ^[a-z0-9][a-z0-9_-]*$ ]]
  [[ ${#name} -le 63 ]]

  if COGLATAS_BROWSER_SMOKE_EMAIL="person@example.com" COGLATAS_BROWSER_SMOKE_PASSWORD="synthetic" validate_fixture_profile; then
    printf 'Expected non-synthetic fixture identity to be rejected.\n' >&2
    return 1
  fi
  COGLATAS_BROWSER_SMOKE_EMAIL="self-test@example.test" COGLATAS_BROWSER_SMOKE_PASSWORD="synthetic" validate_fixture_profile

  temporary_input="$(mktemp)"
  temporary_output="$(mktemp)"
  printf '%s\n' \
    'SYNCFUSION_LICENSE=syncfusion-license-secret' \
    'Password=database-password-secret;Host=postgres' \
    'Cookie: session-cookie-secret' \
    'TOKEN=generic-token-secret' \
    'ACCESS_TOKEN: access-token-secret' \
    'REFRESH_TOKEN=refresh-token-secret' \
    'SECRET = generic-secret-value' \
    'LICENSE: generic-license-value' \
    'export SERVICE_TOKEN=service-token-secret' \
    'client-secret=hyphen-secret-value' \
    'SAFE_VALUE=visible-value' >"$temporary_input"
  SYNCFUSION_LICENSE="syncfusion-license-secret" COGLATAS_BROWSER_SMOKE_PASSWORD="synthetic" sanitize_file "$temporary_input" "$temporary_output"
  for secret in \
    'syncfusion-license-secret' \
    'database-password-secret' \
    'session-cookie-secret' \
    'generic-token-secret' \
    'access-token-secret' \
    'refresh-token-secret' \
    'generic-secret-value' \
    'generic-license-value' \
    'service-token-secret' \
    'hyphen-secret-value'; do
    ! grep -Fq "$secret" "$temporary_output"
  done
  grep -Fq 'TOKEN=[redacted]' "$temporary_output"
  grep -Fq 'ACCESS_TOKEN: [redacted]' "$temporary_output"
  grep -Fq 'REFRESH_TOKEN=[redacted]' "$temporary_output"
  grep -Fq 'SECRET = [redacted]' "$temporary_output"
  grep -Fq 'LICENSE: [redacted]' "$temporary_output"
  grep -Fq 'SAFE_VALUE=visible-value' "$temporary_output"
  grep -Fq 'down --volumes --remove-orphans' "${BASH_SOURCE[0]}"
  grep -Fq '[INFRA/SETUP FAILURE]' "${BASH_SOURCE[0]}"
  grep -Fq '[PRODUCT TEST FAILURE]' "${BASH_SOURCE[0]}"
  rm -f "$temporary_input" "$temporary_output"
  printf 'Functional Compose harness self-test passed.\n'
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
else
  run_harness
fi
