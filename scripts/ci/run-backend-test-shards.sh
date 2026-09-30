#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

# shellcheck source=scripts/ci/parallel-lane-lib.sh
source scripts/ci/parallel-lane-lib.sh

scope="${BACKEND_TEST_SCOPE:-full}"
filter="${BACKEND_TEST_FILTER:-}"

run_test_lane() {
  local lane="$1"
  local lane_filter="$2"
  local results="artifacts/test-results/${lane}"
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

  dotnet test tests/Coglatas.Tests/Coglatas.Tests.csproj "${args[@]}"
}

case "$scope" in
  scoped)
    [[ -n "$filter" ]] || {
      echo "::error::Scoped backend test routing did not provide a filter."
      exit 1
    }
    ci_parallel_start backend-scoped run_test_lane backend-scoped "$filter"
    ;;
  full)
    ci_parallel_start backend-postgresql run_test_lane backend-postgresql \
      "FullyQualifiedName~Coglatas.Tests.PostgreSql"
    ci_parallel_start backend-projects run_test_lane backend-projects \
      "FullyQualifiedName~Coglatas.Tests.Projects|FullyQualifiedName~Coglatas.Tests.Notifications|FullyQualifiedName~Coglatas.Tests.Audit|FullyQualifiedName~Coglatas.Tests.Announcements"
    ci_parallel_start backend-security run_test_lane backend-security \
      "FullyQualifiedName~Coglatas.Tests.Files|FullyQualifiedName~Coglatas.Tests.Security|FullyQualifiedName~Coglatas.Tests.Web|FullyQualifiedName~Coglatas.Tests.Auth|FullyQualifiedName~Coglatas.Tests.Tenancy|FullyQualifiedName~Coglatas.Tests.Workspaces|FullyQualifiedName~Coglatas.Tests.Artifacts|FullyQualifiedName~Coglatas.Tests.Admin"
    ci_parallel_start backend-other run_test_lane backend-other \
      "FullyQualifiedName!~Coglatas.Tests.PostgreSql&FullyQualifiedName!~Coglatas.Tests.Projects&FullyQualifiedName!~Coglatas.Tests.Notifications&FullyQualifiedName!~Coglatas.Tests.Audit&FullyQualifiedName!~Coglatas.Tests.Announcements&FullyQualifiedName!~Coglatas.Tests.Files&FullyQualifiedName!~Coglatas.Tests.Security&FullyQualifiedName!~Coglatas.Tests.Web&FullyQualifiedName!~Coglatas.Tests.Auth&FullyQualifiedName!~Coglatas.Tests.Tenancy&FullyQualifiedName!~Coglatas.Tests.Workspaces&FullyQualifiedName!~Coglatas.Tests.Artifacts&FullyQualifiedName!~Coglatas.Tests.Admin"
    ;;
  *)
    echo "::error::Unexpected BACKEND_TEST_SCOPE '$scope'."
    exit 1
    ;;
esac

ci_parallel_wait
