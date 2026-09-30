#!/usr/bin/env bash
# Shared fail-closed process orchestration for CI lanes.
set -Eeuo pipefail

declare -a CI_PARALLEL_NAMES=()
declare -a CI_PARALLEL_PIDS=()
declare -a CI_PARALLEL_LOGS=()

ci_parallel_start() {
  local name="$1"
  shift

  local root="${CI_PARALLEL_LOG_DIR:-${RUNNER_TEMP:-/tmp}/coglatas-parallel}"
  mkdir -p "$root"
  local log="$root/${name}.log"

  (
    set -Eeuo pipefail
    "$@"
  ) >"$log" 2>&1 &

  CI_PARALLEL_NAMES+=("$name")
  CI_PARALLEL_PIDS+=("$!")
  CI_PARALLEL_LOGS+=("$log")
}

ci_parallel_wait() {
  local failed=0
  local -a statuses=()
  local i rc

  if ((${#CI_PARALLEL_PIDS[@]} == 0)); then
    echo "No parallel CI lanes were selected."
    return 0
  fi

  for i in "${!CI_PARALLEL_PIDS[@]}"; do
    if wait "${CI_PARALLEL_PIDS[$i]}"; then
      rc=0
    else
      rc=$?
      failed=1
    fi
    statuses+=("$rc")
  done

  for i in "${!CI_PARALLEL_NAMES[@]}"; do
    echo "::group::${CI_PARALLEL_NAMES[$i]} (exit ${statuses[$i]})"
    cat "${CI_PARALLEL_LOGS[$i]}" || true
    echo "::endgroup::"
  done

  if ((failed != 0)); then
    echo "::error::One or more parallel CI lanes failed."
    return 1
  fi

  echo "All selected parallel CI lanes passed."
}
