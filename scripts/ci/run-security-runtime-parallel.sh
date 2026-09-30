#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

: "${COGLATAS_SECURITY_CI_PASSWORD:?COGLATAS_SECURITY_CI_PASSWORD is required}"

# shellcheck source=scripts/ci/parallel-lane-lib.sh
source scripts/ci/parallel-lane-lib.sh

run_runtime_lane() {
  local kind="$1"
  export COGLATAS_SECURITY_CI_PROJECT="coglatas-security-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-${kind}"
  export COGLATAS_SECURITY_RUNTIME_STATE_DIR="${RUNNER_TEMP:-/tmp}/coglatas-security-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-${kind}"

  local failed=true
  cleanup() {
    COGLATAS_SECURITY_RUNTIME_FAILED="$failed" \
      bash scripts/ci/run-security-runtime-smoke.sh cleanup || true
  }
  trap cleanup EXIT

  bash scripts/ci/run-security-runtime-smoke.sh prepare
  bash scripts/ci/run-security-runtime-smoke.sh sec03

  case "$kind" in
    core)
      bash scripts/ci/run-security-runtime-smoke.sh sec05
      bash scripts/ci/run-security-runtime-smoke.sh aud02
      ;;
    schemathesis)
      bash scripts/ci/run-security-runtime-smoke.sh sec04
      ;;
    zap)
      bash scripts/ci/run-security-runtime-smoke.sh sec06
      ;;
    *)
      echo "Unknown security runtime lane '$kind'." >&2
      return 2
      ;;
  esac

  failed=false
  cleanup
  trap - EXIT
}

ci_parallel_start security-core run_runtime_lane core
ci_parallel_start security-schemathesis run_runtime_lane schemathesis
ci_parallel_start security-zap run_runtime_lane zap

ci_parallel_wait
