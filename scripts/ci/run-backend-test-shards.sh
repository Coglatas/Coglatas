#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

# shellcheck source=scripts/ci/parallel-lane-lib.sh
source scripts/ci/parallel-lane-lib.sh

scope="${BACKEND_TEST_SCOPE:-full}"
filter="${BACKEND_TEST_FILTER:-}"

prepare_lane_workspace() {
  local lane="$1"
  local lane_root="${RUNNER_TEMP:-/tmp}/coglatas-backend-shards/${lane}"
  rm -rf "$lane_root"
  mkdir -p "$lane_root"
  rsync -a \
    --exclude='.git/' \
    --exclude='artifacts/' \
    --exclude='node_modules/' \
    --exclude='frontend/node_modules/' \
    --exclude='frontend/.angular/' \
    --exclude='frontend/dist/' \
    --exclude='storybook-static/' \
    --exclude='playwright-report/' \
    --exclude='test-results/' \
    "$repo_root/" "$lane_root/"
  printf '%s\n' "$lane_root"
}

run_test_lane() {
  local lane="$1"
  local lane_filter="$2"
  local lane_root="$3"
  local results="$repo_root/artifacts/test-results/${lane}"
  mkdir -p "$results"

  local -a args=(
    --configuration Release
    --no-build
    --disable-build-servers
    -m:1
    --verbosity normal
    --logger "trx;LogFileName=${lane}.trx"
    --results-directory "$results"
    --collect "XPlat Code Coverage"
  )
  if [[ -n "$lane_filter" ]]; then
    args+=(--filter "$lane_filter")
  fi

  cd "$lane_root"
  dotnet test tests/Coglatas.Tests/Coglatas.Tests.csproj "${args[@]}"
}

case "$scope" in
  scoped)
    [[ -n "$filter" ]] || {
      echo "::error::Scoped backend test routing did not provide a filter."
      exit 1
    }
    ci_parallel_start backend-scoped run_test_lane backend-scoped "$filter" "$repo_root"
    ;;
  full)
    postgresql_root="$(prepare_lane_workspace backend-postgresql)"
    projects_root="$(prepare_lane_workspace backend-projects)"
    security_root="$(prepare_lane_workspace backend-security)"
    other_root="$(prepare_lane_workspace backend-other)"

    ci_parallel_start backend-postgresql run_test_lane backend-postgresql \
      "FullyQualifiedName~Coglatas.Tests.PostgreSql" "$postgresql_root"
    ci_parallel_start backend-projects run_test_lane backend-projects \
      "FullyQualifiedName~Coglatas.Tests.Projects|FullyQualifiedName~Coglatas.Tests.Notifications|FullyQualifiedName~Coglatas.Tests.Audit|FullyQualifiedName~Coglatas.Tests.Announcements" "$projects_root"
    ci_parallel_start backend-security run_test_lane backend-security \
      "FullyQualifiedName~Coglatas.Tests.Files|FullyQualifiedName~Coglatas.Tests.Security|FullyQualifiedName~Coglatas.Tests.Web|FullyQualifiedName~Coglatas.Tests.Auth|FullyQualifiedName~Coglatas.Tests.Tenancy|FullyQualifiedName~Coglatas.Tests.Workspaces|FullyQualifiedName~Coglatas.Tests.Artifacts|FullyQualifiedName~Coglatas.Tests.Admin" "$security_root"
    ci_parallel_start backend-other run_test_lane backend-other \
      "FullyQualifiedName!~Coglatas.Tests.PostgreSql&FullyQualifiedName!~Coglatas.Tests.Projects&FullyQualifiedName!~Coglatas.Tests.Notifications&FullyQualifiedName!~Coglatas.Tests.Audit&FullyQualifiedName!~Coglatas.Tests.Announcements&FullyQualifiedName!~Coglatas.Tests.Files&FullyQualifiedName!~Coglatas.Tests.Security&FullyQualifiedName!~Coglatas.Tests.Web&FullyQualifiedName!~Coglatas.Tests.Auth&FullyQualifiedName!~Coglatas.Tests.Tenancy&FullyQualifiedName!~Coglatas.Tests.Workspaces&FullyQualifiedName!~Coglatas.Tests.Artifacts&FullyQualifiedName!~Coglatas.Tests.Admin" "$other_root"
    ;;
  *)
    echo "::error::Unexpected BACKEND_TEST_SCOPE '$scope'."
    exit 1
    ;;
esac

ci_parallel_wait
