#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$ROOT/infra/compose/performance/environment.yml"
RUNTIME_MODE="${COGLATAS_PERFORMANCE_RUNTIME_MODE:-production}"
REUSE_PREBUILT_APP="${COGLATAS_REUSE_PREBUILT_APP_IMAGE:-false}"
COMPOSE_OVERRIDE=""
PROFILE="${COGLATAS_PERFORMANCE_PROFILE:-small}"
PORT="${COGLATAS_PERFORMANCE_PORT:-18080}"
RUN_TOKEN="${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-${BASHPID}"
PROJECT="${COGLATAS_PERFORMANCE_COMPOSE_PROJECT:-coglatas-performance-${RUN_TOKEN}}"
EVIDENCE_DIR="${COGLATAS_PERFORMANCE_EVIDENCE_DIR:-$ROOT/artifacts/performance/${PROFILE}}"
BASE_URL="http://127.0.0.1:${PORT}"
STARTUP_TIMEOUT="${COGLATAS_PERFORMANCE_STARTUP_TIMEOUT_SECONDS:-900}"
COMMAND_TIMEOUT="${COGLATAS_PERFORMANCE_COMMAND_TIMEOUT_SECONDS:-900}"
cleanup_done=0

case "$PROFILE" in
  small|medium|large) ;;
  *) echo "PERF-02: COGLATAS_PERFORMANCE_PROFILE must be small, medium, or large" >&2; exit 2 ;;
esac
if [[ ! "$PROJECT" =~ ^coglatas-performance-[a-z0-9_-]+$ ]]; then
  echo "PERF-02: Compose project must use the dedicated coglatas-performance-* namespace" >&2
  exit 2
fi
if [[ ! "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1024 || PORT > 65535 )); then
  echo "PERF-02: COGLATAS_PERFORMANCE_PORT must be an unprivileged TCP port" >&2
  exit 2
fi
case "$RUNTIME_MODE" in
  production)
    if [[ "$REUSE_PREBUILT_APP" == "true" ]]; then
      if [[ -z "${COGLATAS_APP_IMAGE:-}" ]]; then
        echo "PERF-02: COGLATAS_APP_IMAGE is required when reusing the main runtime image" >&2
        exit 2
      fi
    elif [[ -z "${SYNCFUSION_LICENSE:-}" ]]; then
      echo "PERF-02: SYNCFUSION_LICENSE is required to build the production Angular image" >&2
      exit 2
    fi
    ;;
  source)
    COMPOSE_OVERRIDE="$ROOT/infra/compose/performance/pr.yml"
    ;;
  *)
    echo "PERF-02: COGLATAS_PERFORMANCE_RUNTIME_MODE must be production or source" >&2
    exit 2
    ;;
esac
if [[ -z "${COGLATAS_PERFORMANCE_PASSWORD:-}" ]]; then
  echo "PERF-02: COGLATAS_PERFORMANCE_PASSWORD is required" >&2
  exit 2
fi
for command in docker python3 curl timeout; do
  if ! command -v "$command" >/dev/null; then
    echo "PERF-02: required command is missing: $command" >&2
    exit 2
  fi
done
if ! docker compose version >/dev/null 2>&1; then
  echo "PERF-02: Docker Compose v2 is required" >&2
  exit 2
fi

mkdir -p "$EVIDENCE_DIR"
EVIDENCE_DIR="$(cd "$EVIDENCE_DIR" && pwd)"
# The host collector must be able to unlink files written by the app container.
# Create the capture directory here so it remains owned by the runner.
if [[ "${COGLATAS_PERFORMANCE_DB_CAPTURE_ENABLED:-false}" == "true" ]]; then
  mkdir -p "$EVIDENCE_DIR/db-captures"
  rm -f "$EVIDENCE_DIR/db-captures/"*.json "$EVIDENCE_DIR/db-captures/"*.tmp
fi
export COGLATAS_PERFORMANCE_PROFILE="$PROFILE"
export COGLATAS_PERFORMANCE_PORT="$PORT"
export COGLATAS_PERFORMANCE_EVIDENCE_DIR="$EVIDENCE_DIR"
rm -f \
  "$EVIDENCE_DIR/fixture.json" \
  "$EVIDENCE_DIR/preflight.json" \
  "$EVIDENCE_DIR/warmup.json" \
  "$EVIDENCE_DIR/environment.json"

compose() {
  if [[ -n "$COMPOSE_OVERRIDE" ]]; then
    docker compose -p "$PROJECT" -f "$COMPOSE_FILE" -f "$COMPOSE_OVERRIDE" "$@"
  else
    docker compose -p "$PROJECT" -f "$COMPOSE_FILE" "$@"
  fi
}

cleanup() {
  if (( cleanup_done != 0 )); then
    return 0
  fi
  cleanup_done=1
  set +e
  compose down --volumes --remove-orphans >/dev/null 2>&1
  set -e
}
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'status=$?; trap - EXIT INT TERM; cleanup; exit "$status"' EXIT

app_has_failed() {
  local container_id state exit_code
  container_id="$(compose ps -a -q app 2>/dev/null | head -n 1)"
  [[ -n "$container_id" ]] || return 1
  read -r state exit_code <<< "$(docker inspect --format '{{.State.Status}} {{.State.ExitCode}}' "$container_id" 2>/dev/null || true)"
  [[ "$state" == "exited" || "$state" == "dead" || ( "$exit_code" =~ ^[0-9]+$ && "$exit_code" != "0" ) ]]
}

# Repeated local/CI runs cannot inherit benchmark containers, volumes, or DB rows.
compose down --volumes --remove-orphans >/dev/null 2>&1 || true
docker info >/dev/null
compose config >/dev/null

if [[ "$REUSE_PREBUILT_APP" == "true" ]]; then
  docker image inspect "$COGLATAS_APP_IMAGE" >/dev/null
  compose up -d --no-build postgres migrate app
else
  compose up -d --build postgres migrate app
fi

deadline=$((SECONDS + STARTUP_TIMEOUT))
while (( SECONDS < deadline )); do
  if [[ -s "$EVIDENCE_DIR/fixture.json" ]] && \
     curl --fail --silent --show-error --max-time 5 "$BASE_URL/health/ready" >/dev/null 2>&1; then
    break
  fi
  if app_has_failed; then
    echo "PERF-02: application exited before becoming healthy" >&2
    compose logs --no-color app >&2 || true
    exit 2
  fi
  sleep 2
done
if [[ ! -s "$EVIDENCE_DIR/fixture.json" ]]; then
  echo "PERF-02: fixture evidence was not produced before startup timeout" >&2
  compose logs --no-color app >&2 || true
  exit 2
fi
if ! curl --fail --silent --show-error --max-time 5 "$BASE_URL/health/ready" >/dev/null; then
  echo "PERF-02: application did not become healthy before startup timeout" >&2
  compose logs --no-color app >&2 || true
  exit 2
fi

python3 "$ROOT/scripts/performance/preflight.py" \
  --base-url "$BASE_URL" \
  --profile "$PROFILE" \
  --fixture-evidence "$EVIDENCE_DIR/fixture.json" \
  --output "$EVIDENCE_DIR/preflight.json"

python3 "$ROOT/scripts/performance/warmup.py" \
  --base-url "$BASE_URL" \
  --profile "$PROFILE" \
  --fixture-evidence "$EVIDENCE_DIR/fixture.json" \
  --output "$EVIDENCE_DIR/warmup.json"

collect_args=(
  --compose-project "$PROJECT"
  --compose-file "$COMPOSE_FILE"
  --profile "$PROFILE"
  --fixture-evidence "$EVIDENCE_DIR/fixture.json"
  --output "$EVIDENCE_DIR/environment.json"
)
if [[ -n "$COMPOSE_OVERRIDE" ]]; then
  collect_args+=(--compose-override "$COMPOSE_OVERRIDE")
fi
python3 "$ROOT/scripts/performance/collect-environment.py" "${collect_args[@]}"

export COGLATAS_PERFORMANCE_COMPOSE_PROJECT="$PROJECT"
export COGLATAS_PERFORMANCE_DB_EVIDENCE_PATH="$EVIDENCE_DIR/db-captures"
export COGLATAS_PERFORMANCE_BASE_URL="$BASE_URL"
export COGLATAS_PERFORMANCE_FIXTURE_EVIDENCE="$EVIDENCE_DIR/fixture.json"
export COGLATAS_PERFORMANCE_PREFLIGHT_EVIDENCE="$EVIDENCE_DIR/preflight.json"
export COGLATAS_PERFORMANCE_WARMUP_EVIDENCE="$EVIDENCE_DIR/warmup.json"
export COGLATAS_PERFORMANCE_ENVIRONMENT_EVIDENCE="$EVIDENCE_DIR/environment.json"

if (( $# > 0 )); then
  set +e
  timeout "$COMMAND_TIMEOUT" "$@"
  command_status=$?
  set -e
  if (( command_status != 0 )); then
    if (( command_status == 124 )); then
      echo "PERF-02: benchmark command timed out" >&2
    else
      echo "PERF-02: benchmark command exited with status $command_status" >&2
    fi
    exit "$command_status"
  fi
fi

if [[ -n "${COGLATAS_PERFORMANCE_RESULTS_FILE:-}" ]]; then
  python3 "$ROOT/scripts/performance/verify-samples.py" \
    --results "$COGLATAS_PERFORMANCE_RESULTS_FILE"
fi

echo "PERF-02 environment completed; evidence: $EVIDENCE_DIR"
