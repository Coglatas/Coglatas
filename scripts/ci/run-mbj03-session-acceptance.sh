#!/usr/bin/env bash
set -euo pipefail

BASE_COMPOSE="infra/compose/test/real-backend-smoke.yml"
OVERLAY_COMPOSE="infra/compose/test/acceptance/mbj03-session.yml"
PROJECT_NAME="${COMPOSE_PROJECT_NAME:-coglatas-mbj03-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-$$}"

export COGLATAS_MBJ03_ADMIN_EMAIL="${COGLATAS_MBJ03_ADMIN_EMAIL:-mbj03-system-admin@example.test}"
export COGLATAS_MBJ03_ADMIN_DISPLAY_NAME="${COGLATAS_MBJ03_ADMIN_DISPLAY_NAME:-MBJ03 System Admin}"
export COGLATAS_MBJ03_SUBJECT_EMAIL="${COGLATAS_MBJ03_SUBJECT_EMAIL:-mbj03-session-subject@example.test}"
export COGLATAS_MBJ03_SUBJECT_DISPLAY_NAME="${COGLATAS_MBJ03_SUBJECT_DISPLAY_NAME:-MBJ03 Session Subject}"

if [[ -z "${COGLATAS_MBJ03_ADMIN_PASSWORD:-}" ]]; then
  export COGLATAS_MBJ03_ADMIN_PASSWORD="Coglatas1!$(openssl rand -hex 24)"
fi
if [[ -z "${COGLATAS_MBJ03_OLD_PASSWORD:-}" ]]; then
  export COGLATAS_MBJ03_OLD_PASSWORD="Coglatas1!$(openssl rand -hex 24)"
fi
if [[ -z "${COGLATAS_MBJ03_NEW_PASSWORD:-}" ]]; then
  export COGLATAS_MBJ03_NEW_PASSWORD="Coglatas2!$(openssl rand -hex 24)"
fi
if [[ "$COGLATAS_MBJ03_OLD_PASSWORD" == "$COGLATAS_MBJ03_NEW_PASSWORD" ]]; then
  echo "MBJ-03 old and new passwords must differ." >&2
  exit 1
fi

export COGLATAS_MBJ03_SEED_ADMIN_ENABLED="true"
export COGLATAS_MBJ03_SEED_ADMIN_PASSWORD="$COGLATAS_MBJ03_ADMIN_PASSWORD"

if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
  echo "::add-mask::$COGLATAS_MBJ03_ADMIN_PASSWORD"
  echo "::add-mask::$COGLATAS_MBJ03_OLD_PASSWORD"
  echo "::add-mask::$COGLATAS_MBJ03_NEW_PASSWORD"
fi

compose=(docker compose -p "$PROJECT_NAME" -f "$BASE_COMPOSE" -f "$OVERLAY_COMPOSE")
mkdir -p test-results

remove_private_state() {
  docker run --rm -v "$PWD:/work" alpine:3.21 \
    sh -c 'rm -rf /work/test-results/.mbj03-private' >/dev/null 2>&1 || true
}

cleanup() {
  local exit_code=$?
  remove_private_state
  if (( exit_code != 0 )); then
    echo "Collecting sanitized MBJ-03 failure evidence." >&2
    "${compose[@]}" ps --all > test-results/mbj03-compose-ps.txt 2>&1 || true
    "${compose[@]}" logs --no-color --tail 500 postgres migrate app > /tmp/mbj03-compose.log 2>&1 || true
    python3 -c '
import os, sys
text = sys.stdin.read()
for name in ("COGLATAS_MBJ03_ADMIN_PASSWORD", "COGLATAS_MBJ03_OLD_PASSWORD", "COGLATAS_MBJ03_NEW_PASSWORD"):
    secret = os.environ.get(name, "")
    if secret:
        text = text.replace(secret, "[REDACTED]")
sys.stdout.write(text)
' < /tmp/mbj03-compose.log > test-results/mbj03-compose.log 2>/dev/null || true
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

run_probe_phase() {
  local phase="$1"
  "${compose[@]}" run --build --rm --no-deps real-backend-playwright \
    bash -lc "npm ci && node tests/ui/mbj03-session-acceptance.mjs '$phase'"
}

read_dp_anchor() {
  "${compose[@]}" exec -T app sh -lc '
set -eu
file="$(find /app/data/protection-keys -maxdepth 1 -type f -name "*.xml" | sort | head -n 1)"
test -n "$file"
printf "%s|%s\n" "$(basename "$file")" "$(sha256sum "$file" | awk "{print \$1}")"
'
}

verify_dp_anchor() {
  local anchor_name="$1" anchor_hash="$2"
  local current_hash
  current_hash="$("${compose[@]}" exec -T app sh -lc \
    "test -f '/app/data/protection-keys/$anchor_name' && sha256sum '/app/data/protection-keys/$anchor_name' | awk '{print \$1}'")"
  [[ "$current_hash" == "$anchor_hash" ]]
}

expire_subject_session() {
  local count
  count="$("${compose[@]}" exec -T postgres \
    psql -U coglatas_smoke -d coglatas_smoke -v ON_ERROR_STOP=1 -At \
      -v email="$COGLATAS_MBJ03_SUBJECT_EMAIL" <<'SQL'
WITH target AS (
  SELECT s."Id"
  FROM sessions s
  JOIN users u ON u."Id" = s."UserId"
  WHERE u."NormalizedEmail" = upper(:'email')
    AND s."RevokedAt" IS NULL
    AND s."ExpiresAt" > now()
), updated AS (
  UPDATE sessions s
  SET "ExpiresAt" = now() - interval '1 minute'
  FROM target
  WHERE s."Id" = target."Id"
  RETURNING s."Id"
)
SELECT count(*) FROM updated;
SQL
)"
  if [[ "$count" != "1" ]]; then
    echo "MBJ-03 expected exactly one active subject session to expire; updated $count." >&2
    return 1
  fi
}

verify_postgres_state() {
  local row
  row="$("${compose[@]}" exec -T postgres \
    psql -U coglatas_smoke -d coglatas_smoke -v ON_ERROR_STOP=1 -At -F '|' \
      -v email="$COGLATAS_MBJ03_SUBJECT_EMAIL" <<'SQL'
WITH subject AS (
  SELECT "Id", "Status"
  FROM users
  WHERE "NormalizedEmail" = upper(:'email')
    AND "DeletedAt" IS NULL
), subject_sessions AS (
  SELECT s.*
  FROM sessions s
  JOIN subject u ON u."Id" = s."UserId"
), subject_revocations AS (
  SELECT a.*
  FROM audit_logs a
  JOIN subject u ON a."MetadataJson"->>'targetUserId' = u."Id"::text
  WHERE a."Action" = 'SessionRevoked'
)
SELECT
  (SELECT count(*) FROM subject),
  (SELECT count(*) FROM subject WHERE "Status" = 'Active'),
  (SELECT count(*) FROM subject_sessions),
  (SELECT count(*) FROM subject_sessions WHERE "RevokedAt" IS NOT NULL),
  (SELECT count(*) FROM subject_sessions WHERE "RevokedAt" IS NULL AND "ExpiresAt" <= now()),
  (SELECT count(*) FROM subject_sessions WHERE "RevokedAt" IS NULL AND "ExpiresAt" > now()),
  (SELECT count(*) FROM audit_logs a JOIN subject u ON a."EntityId" = u."Id" WHERE a."Action" = 'PasswordChanged'),
  (SELECT count(*) FROM audit_logs a JOIN subject u ON a."EntityId" = u."Id" WHERE a."Action" = 'Logout'),
  (SELECT count(*) FROM audit_logs a JOIN subject u ON a."EntityId" = u."Id" WHERE a."Action" = 'UserSuspended'),
  (SELECT count(*) FROM audit_logs a JOIN subject u ON a."EntityId" = u."Id" WHERE a."Action" = 'UserActivated'),
  (SELECT count(*) FROM subject_revocations WHERE "MetadataJson"->>'reason' = 'PasswordChanged'),
  (SELECT count(*) FROM subject_revocations WHERE "MetadataJson"->>'reason' = 'Logout'),
  (SELECT count(*) FROM subject_revocations WHERE "MetadataJson"->>'reason' = 'UserSuspended');
SQL
)"

  local subject_count active_subject_count total_sessions revoked_sessions expired_sessions active_sessions
  local password_changed_count logout_count suspended_count activated_count
  local password_revoked_count logout_revoked_count suspended_revoked_count
  IFS='|' read -r \
    subject_count active_subject_count total_sessions revoked_sessions expired_sessions active_sessions \
    password_changed_count logout_count suspended_count activated_count \
    password_revoked_count logout_revoked_count suspended_revoked_count <<< "$row"

  if [[ "$subject_count" != "1" ||
        "$active_subject_count" != "1" ||
        "$total_sessions" -lt 5 ||
        "$revoked_sessions" -lt 3 ||
        "$expired_sessions" -lt 1 ||
        "$active_sessions" != "1" ||
        "$password_changed_count" -lt 1 ||
        "$logout_count" -lt 1 ||
        "$suspended_count" -lt 1 ||
        "$activated_count" -lt 1 ||
        "$password_revoked_count" -lt 1 ||
        "$logout_revoked_count" -lt 1 ||
        "$suspended_revoked_count" -lt 1 ]]; then
    echo "MBJ-03 PostgreSQL state mismatch: subject=$subject_count activeSubject=$active_subject_count totalSessions=$total_sessions revoked=$revoked_sessions expired=$expired_sessions activeSessions=$active_sessions passwordChanged=$password_changed_count logout=$logout_count suspended=$suspended_count activated=$activated_count passwordRevoked=$password_revoked_count logoutRevoked=$logout_revoked_count suspendedRevoked=$suspended_revoked_count" >&2
    return 1
  fi

  cat > test-results/mbj03-postgres-state.json <<JSON
{
  "journey": "MBJ-03",
  "subjectCount": $subject_count,
  "activeSubjectCount": $active_subject_count,
  "totalSubjectSessions": $total_sessions,
  "revokedSubjectSessions": $revoked_sessions,
  "expiredUnrevokedSubjectSessions": $expired_sessions,
  "activeSubjectSessions": $active_sessions,
  "passwordChangedAuditCount": $password_changed_count,
  "logoutAuditCount": $logout_count,
  "userSuspendedAuditCount": $suspended_count,
  "userActivatedAuditCount": $activated_count,
  "passwordChangedSessionRevokedAuditCount": $password_revoked_count,
  "logoutSessionRevokedAuditCount": $logout_revoked_count,
  "userSuspendedSessionRevokedAuditCount": $suspended_revoked_count,
  "sessionRevocationAuditsBoundToSubject": true,
  "secretMaterialRecorded": false
}
JSON
}

remove_private_state
echo "Validating MBJ-03 Compose configuration."
"${compose[@]}" config --quiet

echo "Starting isolated PostgreSQL and MBJ-03 application stack."
"${compose[@]}" up --build --detach postgres app
wait_healthy postgres
wait_healthy app

run_probe_phase phase1

app_id_before="$("${compose[@]}" ps -q app)"
IFS='|' read -r dp_anchor_name dp_anchor_hash <<< "$(read_dp_anchor)"
if [[ -z "$dp_anchor_name" || -z "$dp_anchor_hash" ]]; then
  echo "MBJ-03 could not capture a persisted Data Protection key anchor." >&2
  exit 1
fi

echo "Restarting application with administrator seeding disabled while preserving PostgreSQL and Data Protection volumes."
export COGLATAS_MBJ03_SEED_ADMIN_ENABLED="false"
export COGLATAS_MBJ03_SEED_ADMIN_PASSWORD=""
"${compose[@]}" up --detach --force-recreate --no-deps app
wait_healthy app

app_id_after="$("${compose[@]}" ps -q app)"
if [[ -z "$app_id_before" || -z "$app_id_after" || "$app_id_before" == "$app_id_after" ]]; then
  echo "MBJ-03 application restart did not replace the app container." >&2
  exit 1
fi
if ! verify_dp_anchor "$dp_anchor_name" "$dp_anchor_hash"; then
  echo "MBJ-03 Data Protection key anchor was not preserved across restart." >&2
  exit 1
fi

cat > test-results/mbj03-restart-state.json <<JSON
{
  "journey": "MBJ-03",
  "appContainerRecreated": true,
  "dataProtectionKeyPreserved": true,
  "administratorSeedDisabledOnRestart": true,
  "secretMaterialRecorded": false
}
JSON

run_probe_phase phase2
expire_subject_session
run_probe_phase phase3
verify_postgres_state
remove_private_state

echo "MBJ-03 session lifecycle acceptance passed."
