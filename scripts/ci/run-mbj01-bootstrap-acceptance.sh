#!/usr/bin/env bash
set -euo pipefail

BASE_COMPOSE="infra/compose/test/real-backend-smoke.yml"
OVERLAY_COMPOSE="infra/compose/test/acceptance/mbj01-bootstrap.yml"
PROJECT_NAME="${COMPOSE_PROJECT_NAME:-coglatas-mbj01-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-$$}"

export COGLATAS_MBJ01_BOOTSTRAP_EMAIL="${COGLATAS_MBJ01_BOOTSTRAP_EMAIL:-mbj01-bootstrap-admin@example.test}"
export COGLATAS_MBJ01_BOOTSTRAP_DISPLAY_NAME="${COGLATAS_MBJ01_BOOTSTRAP_DISPLAY_NAME:-MBJ01 Bootstrap Admin}"
if [[ -z "${COGLATAS_MBJ01_BOOTSTRAP_PASSWORD:-}" ]]; then
  COGLATAS_MBJ01_BOOTSTRAP_PASSWORD="Coglatas1!$(openssl rand -hex 24)"
  export COGLATAS_MBJ01_BOOTSTRAP_PASSWORD
fi
export COGLATAS_MBJ01_APP_SEED_PASSWORD="$COGLATAS_MBJ01_BOOTSTRAP_PASSWORD"
export COGLATAS_MBJ01_SEED_ENABLED="true"

if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
  echo "::add-mask::$COGLATAS_MBJ01_BOOTSTRAP_PASSWORD"
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
    echo "Collecting sanitized MBJ-01 failure evidence." >&2
    "${compose[@]}" ps --all > test-results/mbj01-compose-ps.txt 2>&1 || true
    "${compose[@]}" logs --no-color --tail 300 postgres migrate app > /tmp/mbj01-compose.log 2>&1 || true
    python3 -c '
import os
import sys
text = sys.stdin.read()
secret = os.environ.get("COGLATAS_MBJ01_BOOTSTRAP_PASSWORD", "")
if secret:
    text = text.replace(secret, "[REDACTED]")
sys.stdout.write(text)
' < /tmp/mbj01-compose.log > test-results/mbj01-compose.log 2>/dev/null || true
  fi
  "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  return "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

wait_healthy() {
  local service="$1"
  local deadline=$((SECONDS + 240))
  local last_state="not-created"

  while (( SECONDS < deadline )); do
    local container_id
    container_id="$("${compose[@]}" ps --all -q "$service" 2>/dev/null | head -n 1)"
    if [[ -n "$container_id" ]]; then
      last_state="$(docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{.State.ExitCode}}' "$container_id" 2>/dev/null || true)"
      if [[ "$last_state" == running\ healthy\ * ]]; then
        return 0
      fi
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

run_probe() {
  local phase="$1"
  "${compose[@]}" run --build --rm real-backend-playwright \
    bash -lc "npm ci && node tests/ui/mbj01-bootstrap-acceptance.mjs '$phase'"
}

verify_postgres_state() {
  local phase="$1"
  local row
  row="$("${compose[@]}" exec -T postgres \
    psql -U coglatas_smoke -d coglatas_smoke \
      -v ON_ERROR_STOP=1 \
      -v email="$COGLATAS_MBJ01_BOOTSTRAP_EMAIL" \
      -At -F '|' <<'SQL'
SELECT
  (SELECT count(*)
     FROM users u
    WHERE u."NormalizedEmail" = upper(:'email')
      AND u."SystemRole" IN ('PlatformAdmin', 'SystemAdmin')
      AND u."Status" = 'Active'),
  (SELECT coalesce(max(u."SystemRole"), '')
     FROM users u
    WHERE u."NormalizedEmail" = upper(:'email')
      AND u."Status" = 'Active'),
  (SELECT count(*)
     FROM tenant_users tu
     JOIN users u ON u."Id" = tu."UserId"
    WHERE u."NormalizedEmail" = upper(:'email')
      AND tu."Role" = 'Owner'
      AND tu."Status" = 'Active'),
  (SELECT count(*)
     FROM workspace_members wm
     JOIN users u ON u."Id" = wm."UserId"
     JOIN workspaces w ON w."Id" = wm."WorkspaceId"
    WHERE u."NormalizedEmail" = upper(:'email')
      AND wm."Role" = 'Owner'
      AND wm."Status" = 'Active'
      AND w."Slug" = 'default-workspace'
      AND w."Status" = 'Active'),
  (SELECT count(*)
     FROM workspaces w
    WHERE w."Slug" = 'default-workspace'
      AND w."Status" = 'Active');
SQL
)"

  local administrator_count administrator_role tenant_membership_count workspace_membership_count workspace_count
  IFS='|' read -r administrator_count administrator_role tenant_membership_count workspace_membership_count workspace_count <<< "$row"
  if [[ "$administrator_count" != "1" ||
        ( "$administrator_role" != "PlatformAdmin" && "$administrator_role" != "SystemAdmin" ) ||
        "$tenant_membership_count" != "1" ||
        "$workspace_membership_count" != "1" ||
        "$workspace_count" != "1" ]]; then
    echo "MBJ-01 PostgreSQL state mismatch in phase '$phase': administrator=$administrator_count role=$administrator_role tenantMembership=$tenant_membership_count workspaceMembership=$workspace_membership_count workspace=$workspace_count" >&2
    return 1
  fi

  cat > "test-results/mbj01-postgres-${phase}.json" <<JSON
{
  "journey": "MBJ-01",
  "phase": "$phase",
  "activeAdministratorCount": $administrator_count,
  "administratorRole": "$administrator_role",
  "acceptedAdministratorRoleAliases": ["PlatformAdmin", "SystemAdmin"],
  "activeTenantOwnerMembershipCount": $tenant_membership_count,
  "activeDefaultWorkspaceOwnerMembershipCount": $workspace_membership_count,
  "activeDefaultWorkspaceCount": $workspace_count,
  "passwordMaterialRecorded": false
}
JSON
  echo "MBJ-01 PostgreSQL state verified for phase '$phase' with administrator role '$administrator_role'."
}

echo "Validating MBJ-01 Compose configuration."
"${compose[@]}" config --quiet

echo "Starting fresh PostgreSQL and bootstrap-enabled application."
"${compose[@]}" up --build --detach postgres app
wait_healthy postgres
wait_healthy app
run_probe initial
verify_postgres_state initial

echo "Recreating only the application with administrator seed disabled and seed password cleared."
export COGLATAS_MBJ01_SEED_ENABLED="false"
export COGLATAS_MBJ01_APP_SEED_PASSWORD=""
"${compose[@]}" up --detach --no-deps --force-recreate app
wait_healthy app
run_probe restart
verify_postgres_state restart

echo "MBJ-01 fresh authorized bootstrap acceptance passed across initial startup and seed-disabled restart."
