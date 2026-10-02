#!/usr/bin/env bash
set -euo pipefail

BASE_COMPOSE="infra/compose/test/real-backend-smoke.yml"
OVERLAY_COMPOSE="infra/compose/test/acceptance/mbj02-invite.yml"
PROJECT_NAME="${COMPOSE_PROJECT_NAME:-coglatas-mvpa-authz-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-$$}"

export COGLATAS_MBJ02_ADMIN_EMAIL="${COGLATAS_MBJ02_ADMIN_EMAIL:-mvpa-authz-system-admin@example.test}"
export COGLATAS_MBJ02_ADMIN_DISPLAY_NAME="${COGLATAS_MBJ02_ADMIN_DISPLAY_NAME:-MVP-A AuthZ System Admin}"
export COGLATAS_MBJ02_ADMIN_PASSWORD="${COGLATAS_MBJ02_ADMIN_PASSWORD:-Coglatas1!$(openssl rand -hex 24)}"
export COGLATAS_MBJ02_INVITEE_EMAIL="${COGLATAS_MBJ02_INVITEE_EMAIL:-mvpa-authz-member@example.test}"
export COGLATAS_MBJ02_INVITEE_DISPLAY_NAME="${COGLATAS_MBJ02_INVITEE_DISPLAY_NAME:-MVP-A AuthZ Member}"
export COGLATAS_MBJ02_INVITEE_PASSWORD="${COGLATAS_MBJ02_INVITEE_PASSWORD:-Coglatas1!$(openssl rand -hex 24)}"

# The MBJ-02 overlay requires these synthetic values even though this focused
# boundary suite does not exercise the corresponding MBJ-02 scenarios.
export COGLATAS_MBJ02_REVOKED_EMAIL="${COGLATAS_MBJ02_REVOKED_EMAIL:-mvpa-authz-unused-revoked@example.test}"
export COGLATAS_MBJ02_EXPIRED_EMAIL="${COGLATAS_MBJ02_EXPIRED_EMAIL:-mvpa-authz-unused-expired@example.test}"
export COGLATAS_MBJ02_MISMATCH_TARGET_EMAIL="${COGLATAS_MBJ02_MISMATCH_TARGET_EMAIL:-mvpa-authz-unused-target@example.test}"
export COGLATAS_MBJ02_MISMATCH_OTHER_EMAIL="${COGLATAS_MBJ02_MISMATCH_OTHER_EMAIL:-mvpa-authz-unused-other@example.test}"
export COGLATAS_MBJ02_CROSS_TENANT_EMAIL="${COGLATAS_MBJ02_CROSS_TENANT_EMAIL:-mvpa-authz-unused-cross@example.test}"
export COGLATAS_MBJ02_CROSS_TENANT_TOKEN="${COGLATAS_MBJ02_CROSS_TENANT_TOKEN:-mvpa-authz-unused-$(openssl rand -hex 24)}"
export COGLATAS_MBJ02_CROSS_TENANT_WORKSPACE_ID="${COGLATAS_MBJ02_CROSS_TENANT_WORKSPACE_ID:-22222222-2222-2222-2222-222222222223}"

if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
  echo "::add-mask::$COGLATAS_MBJ02_ADMIN_PASSWORD"
  echo "::add-mask::$COGLATAS_MBJ02_INVITEE_PASSWORD"
  echo "::add-mask::$COGLATAS_MBJ02_CROSS_TENANT_TOKEN"
fi

compose=(docker compose -p "$PROJECT_NAME" -f "$BASE_COMPOSE" -f "$OVERLAY_COMPOSE")
mkdir -p test-results
if [[ ! -w test-results ]]; then
  uid="$(id -u)"
  gid="$(id -g)"
  echo "Repairing test-results ownership left by a previous Docker acceptance lane."
  docker run --rm \
    -v "$PWD/test-results:/results" \
    alpine:3.21 \
    sh -c "chown -R ${uid}:${gid} /results"
fi

cleanup() {
  local exit_code=$?
  if (( exit_code != 0 )); then
    echo "Collecting sanitized MVP-A AuthZ failure evidence." >&2
    "${compose[@]}" ps --all > test-results/mvp-a-authz-compose-ps.txt 2>&1 || true
    "${compose[@]}" logs --no-color --tail 300 postgres migrate app > /tmp/mvp-a-authz-compose.log 2>&1 || true
    python3 -c '
import os, sys
text = sys.stdin.read()
for name in ("COGLATAS_MBJ02_ADMIN_PASSWORD", "COGLATAS_MBJ02_INVITEE_PASSWORD", "COGLATAS_MBJ02_CROSS_TENANT_TOKEN"):
    secret = os.environ.get(name, "")
    if secret:
        text = text.replace(secret, "[REDACTED]")
sys.stdout.write(text)
' < /tmp/mvp-a-authz-compose.log > test-results/mvp-a-authz-compose.log 2>/dev/null || true
  fi
  "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  return "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

wait_healthy() {
  local service="$1" deadline=$((SECONDS + 240)) last_state="not-created"
  while (( SECONDS < deadline )); do
    local container_id
    container_id="$("${compose[@]}" ps --all -q "$service" 2>/dev/null | head -n 1)"
    if [[ -n "$container_id" ]]; then
      last_state="$(docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{.State.ExitCode}}' "$container_id" 2>/dev/null || true)"
      [[ "$last_state" == running\ healthy\ * ]] && return 0
      if [[ "$last_state" == exited\ * || "$last_state" == dead\ * ]]; then
        echo "$service stopped before becoming healthy: $last_state" >&2
        return 1
      fi
    fi
    sleep 1
  done
  echo "Timed out waiting for $service to become healthy. Last state: $last_state" >&2
  return 1
}

echo "Validating MVP-A AuthZ Compose configuration."
"${compose[@]}" config --quiet

echo "Starting isolated PostgreSQL and authorization-boundary application stack."
"${compose[@]}" up --build --detach postgres app
wait_healthy postgres
wait_healthy app

"${compose[@]}" run --build --rm real-backend-playwright \
  bash -lc "npm ci && bash scripts/ci/mvp-a-authz-boundary-acceptance.sh"

echo "MVP-A real-backend authorization boundary acceptance passed."
