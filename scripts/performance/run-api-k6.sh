#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MODE="${1:-fast}"
OUT="$ROOT/artifacts/performance/api"
case "$MODE" in fast|regression) ;; *) exit 2 ;; esac
mkdir -p "$OUT"
# Never accept stale evidence from an earlier cancelled/partial run.
rm -f "$OUT"/*.json "$OUT"/*.md
export COGLATAS_PERFORMANCE_RUNTIME_MODE=source
export COGLATAS_PERFORMANCE_PROFILE=small
python3 "$ROOT/scripts/performance/api_k6.py" validate
HEAD_SHA="$(git -C "$ROOT" rev-parse HEAD)"
BASELINE_SHA="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["baseline"]["sha"])' "$ROOT/performance/api-k6.json")"
RUNS=1
BASELINE_ROOT=""
cleanup() {
  if [[ -n "$BASELINE_ROOT" ]]; then
    git -C "$ROOT" worktree remove --force "$BASELINE_ROOT" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT
if [[ "$MODE" == regression ]]; then
  RUNS="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["mainRuns"])' "$ROOT/performance/api-k6.json")"
  git -C "$ROOT" merge-base --is-ancestor "$BASELINE_SHA" origin/main
  test "$BASELINE_SHA" != "$HEAD_SHA"
  BASELINE_ROOT="$(mktemp -d)"
  git -C "$ROOT" worktree add --detach "$BASELINE_ROOT" "$BASELINE_SHA"
fi
current=()
baseline=()
for (( run=1; run<=RUNS; run++ )); do
  # Pair baseline/current on one runner. A fresh PERF-02 stack/DB per group
  # provides deterministic mutation reset and prevents cross-run state reuse.
  if [[ "$MODE" == regression ]]; then
    GITHUB_SHA="$BASELINE_SHA" \
      COGLATAS_PERFORMANCE_COMPOSE_PROJECT="coglatas-performance-api-baseline-${GITHUB_RUN_ID:-local}-$run" \
      COGLATAS_PERFORMANCE_EVIDENCE_DIR="$OUT/baseline-environment-$run" \
      bash "$BASELINE_ROOT/scripts/performance/with-environment.sh" \
      python3 "$ROOT/scripts/performance/api_k6.py" collect --output "$OUT/baseline-$run.json"
    baseline+=("$OUT/baseline-$run.json")
  fi
  GITHUB_SHA="$HEAD_SHA" \
    COGLATAS_PERFORMANCE_COMPOSE_PROJECT="coglatas-performance-api-current-${GITHUB_RUN_ID:-local}-$run" \
    COGLATAS_PERFORMANCE_EVIDENCE_DIR="$OUT/current-environment-$run" \
    bash "$ROOT/scripts/performance/with-environment.sh" \
    python3 "$ROOT/scripts/performance/api_k6.py" collect --output "$OUT/current-$run.json"
  current+=("$OUT/current-$run.json")
done
args=(evaluate --mode "$MODE" --current "${current[@]}" --output "$OUT/result.json")
if [[ "$MODE" == regression ]]; then args+=(--baseline "${baseline[@]}"); fi
python3 "$ROOT/scripts/performance/api_k6.py" "${args[@]}"
