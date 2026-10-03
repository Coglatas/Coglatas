#!/usr/bin/env bash
set -Eeuo pipefail

router="$(realpath scripts/ci/route-main-ci-changes.sh)"
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT

init_repo() {
  local repo="$1"
  shift
  mkdir -p "$repo"
  git -C "$repo" init -q
  git -C "$repo" config user.name "CI Router Test"
  git -C "$repo" config user.email "ci-router@example.invalid"

  local scope
  for scope in "$@"; do
    mkdir -p "$repo/tests/Coglatas.Tests/$scope"
    : > "$repo/tests/Coglatas.Tests/$scope/.keep"
  done
}

commit_all() {
  local repo="$1"
  local message="$2"
  git -C "$repo" add .
  git -C "$repo" commit -qm "$message"
  git -C "$repo" rev-parse HEAD
}

route_repo() {
  local repo="$1"
  local base="$2"
  local head="$3"
  local output="$repo/router-output.txt"
  local summary="$repo/router-summary.txt"
  : > "$output"
  : > "$summary"

  (
    cd "$repo"
    BASE_SHA="$base" \
    HEAD_SHA="$head" \
    GITHUB_OUTPUT="$output" \
    GITHUB_STEP_SUMMARY="$summary" \
    RUNNER_TEMP="$repo" \
      bash "$router"
  )

  printf '%s\n' "$output"
}

value_of() {
  local output="$1"
  local key="$2"
  sed -n "s/^${key}=//p" "$output" | tail -n 1
}

assert_eq() {
  local expected="$1"
  local actual="$2"
  local label="$3"
  if [[ "$actual" != "$expected" ]]; then
    echo "router assertion failed: $label expected=$expected actual=$actual" >&2
    exit 1
  fi
}

assert_contains() {
  local haystack="$1"
  local needle="$2"
  local label="$3"
  if [[ "$haystack" != *"$needle"* ]]; then
    echo "router assertion failed: $label missing '$needle' in '$haystack'" >&2
    exit 1
  fi
}

# Announcement runtime + persistence + migration should remain scoped, while EF
# validation is enabled and unrelated TASK-V1 PR07 suites remain off.
repo="$tmp_root/announcement"
init_repo "$repo" Announcements PostgreSql
mkdir -p \
  "$repo/src/Coglatas.Application/Announcements" \
  "$repo/src/Coglatas.Infrastructure/Persistence/Migrations" \
  "$repo/src/Coglatas.Infrastructure/Persistence"
printf 'public sealed class AnnouncementDraftService {}\n' > "$repo/src/Coglatas.Application/Announcements/AnnouncementDraftService.cs"
printf 'public sealed class AnnouncementRepository {}\n' > "$repo/src/Coglatas.Infrastructure/Persistence/AnnouncementRepository.cs"
base="$(commit_all "$repo" base)"
printf 'public sealed class AnnouncementDraftService { public string Announcement => "changed"; }\n' > "$repo/src/Coglatas.Application/Announcements/AnnouncementDraftService.cs"
printf 'public sealed class AnnouncementRepository { public string Announcement => "changed"; }\n' > "$repo/src/Coglatas.Infrastructure/Persistence/AnnouncementRepository.cs"
printf 'public sealed class AddAnnouncementDistributionTargets {}\n' > "$repo/src/Coglatas.Infrastructure/Persistence/Migrations/20260901000000_AddAnnouncementDistributionTargets.cs"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq true "$(value_of "$output" backend)" "announcement backend"
assert_eq true "$(value_of "$output" backend_ef)" "announcement EF"
assert_eq true "$(value_of "$output" backend_tests)" "announcement tests"
assert_eq scoped "$(value_of "$output" backend_test_scope)" "announcement scope"
filter="$(value_of "$output" backend_test_filter)"
assert_contains "$filter" 'Coglatas.Tests.Announcements' "announcement filter"
assert_contains "$filter" 'Coglatas.Tests.PostgreSql' "announcement persistence filter"
assert_eq false "$(value_of "$output" backend_pr07b)" "announcement PR07-B"
assert_eq false "$(value_of "$output" backend_pr07c)" "announcement PR07-C"
assert_eq false "$(value_of "$output" backend_pr07d)" "announcement PR07-D"

# Shared DI is content-aware: an Announcement-only registration must not widen
# normal backend tests to the full suite.
repo="$tmp_root/shared-di"
init_repo "$repo" Announcements
mkdir -p "$repo/src/Coglatas.Infrastructure"
printf 'public static class DependencyInjection {}\n' > "$repo/src/Coglatas.Infrastructure/DependencyInjection.cs"
base="$(commit_all "$repo" base)"
printf '%s\n' \
  'public static class DependencyInjection {' \
  '  // Announcement registration' \
  '  // services.AddScoped<IAnnouncementDistributionStore, AnnouncementDistributionStore>();' \
  '}' > "$repo/src/Coglatas.Infrastructure/DependencyInjection.cs"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq scoped "$(value_of "$output" backend_test_scope)" "shared DI scope"
assert_contains "$(value_of "$output" backend_test_filter)" 'Coglatas.Tests.Announcements' "shared DI filter"

# Task comments / mentions / assignments route the Projects namespace plus the
# focused TASK-V1-PR07-B gate, without enabling C or D.
repo="$tmp_root/pr07b"
init_repo "$repo" Projects
mkdir -p "$repo/src/Coglatas.Application/Projects"
printf 'public sealed class TaskSubresourceService {}\n' > "$repo/src/Coglatas.Application/Projects/TaskSubresourceService.cs"
base="$(commit_all "$repo" base)"
printf 'public sealed class TaskSubresourceService { /* TaskComment Mention Assignee */ }\n' > "$repo/src/Coglatas.Application/Projects/TaskSubresourceService.cs"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq scoped "$(value_of "$output" backend_test_scope)" "PR07-B scope"
assert_contains "$(value_of "$output" backend_test_filter)" 'Coglatas.Tests.Projects' "PR07-B filter"
assert_eq true "$(value_of "$output" backend_pr07b)" "PR07-B route"
assert_eq false "$(value_of "$output" backend_pr07c)" "PR07-C isolation"
assert_eq false "$(value_of "$output" backend_pr07d)" "PR07-D isolation"

# A normal backend test file can carry no TASK-V1-PR07 traits. Classifying it
# must remain successful under set -e and must not fabricate a PR07 gate.
repo="$tmp_root/ordinary-backend-test"
init_repo "$repo" Projects
printf '%s\n' \
  'namespace Coglatas.Tests.Projects;' \
  'public sealed class ResearchPlanServiceTests { /* Issue364 */ }' \
  > "$repo/tests/Coglatas.Tests/Projects/ResearchPlanServiceTests.cs"
base="$(commit_all "$repo" base)"
printf '%s\n' \
  'namespace Coglatas.Tests.Projects;' \
  'public sealed class ResearchPlanServiceTests { /* Issue364 Issue366 */ }' \
  > "$repo/tests/Coglatas.Tests/Projects/ResearchPlanServiceTests.cs"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq true "$(value_of "$output" backend)" "ordinary backend test backend"
assert_eq true "$(value_of "$output" backend_tests)" "ordinary backend test tests"
assert_eq scoped "$(value_of "$output" backend_test_scope)" "ordinary backend test scope"
assert_contains "$(value_of "$output" backend_test_filter)" 'Coglatas.Tests.Projects' "ordinary backend test filter"
assert_eq false "$(value_of "$output" backend_pr07b)" "ordinary backend test PR07-B"
assert_eq false "$(value_of "$output" backend_pr07c)" "ordinary backend test PR07-C"
assert_eq false "$(value_of "$output" backend_pr07d)" "ordinary backend test PR07-D"

# Production backend changes must also route the authenticated runtime security
# gate. A scoped unit-test route is not sufficient for OpenAPI/Schemathesis and
# authorization-contract changes.
repo="$tmp_root/security-runtime-source"
init_repo "$repo" Projects
mkdir -p "$repo/src/Coglatas.Application/Projects"
printf 'public sealed class ProjectMutationService {}\n' > "$repo/src/Coglatas.Application/Projects/ProjectMutationService.cs"
base="$(commit_all "$repo" base)"
printf 'public sealed class ProjectMutationService { public int Version => 2; }\n' > "$repo/src/Coglatas.Application/Projects/ProjectMutationService.cs"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq true "$(value_of "$output" security)" "runtime source security"
assert_eq true "$(value_of "$output" avmig_contract)" "runtime source AV-MIG contract"
assert_eq false "$(value_of "$output" avmig_selftests)" "runtime source verifier self-tests"
assert_eq false "$(value_of "$output" security_dotnet)" "runtime source dependency scan"
assert_eq true "$(value_of "$output" security_compose)" "runtime source security compose"

# Security harness changes must test the harness itself rather than passing on
# static routing only.
repo="$tmp_root/security-runtime-harness"
init_repo "$repo"
mkdir -p "$repo/scripts/security"
printf 'echo base\n' > "$repo/scripts/security/schemathesis-runner.sh"
base="$(commit_all "$repo" base)"
printf 'echo changed\n' > "$repo/scripts/security/schemathesis-runner.sh"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq true "$(value_of "$output" security_compose)" "security harness compose"
assert_eq false "$(value_of "$output" security_dotnet)" "security harness dependency scan"

# AV-MIG contract verification depends on the effective .NET SDK/build
# configuration as well as source files. Each of these inputs must route the
# client-independent contract gate even when it is the only changed file.
for avmig_input in \
  global.json \
  NuGet.config \
  Directory.Build.props \
  src/Coglatas.Web/Coglatas.Web.csproj; do
  slug="${avmig_input//\//-}"
  repo="$tmp_root/avmig-build-${slug//./-}"
  init_repo "$repo"
  printf 'base\n' > "$repo/README.md"
  base="$(commit_all "$repo" base)"
  mkdir -p "$(dirname "$repo/$avmig_input")"
  printf 'changed\n' > "$repo/$avmig_input"
  head="$(commit_all "$repo" head)"
  output="$(route_repo "$repo" "$base" "$head")"
  assert_eq true "$(value_of "$output" avmig_contract)" "AV-MIG build input $avmig_input"
  assert_eq true "$(value_of "$output" avmig_selftests)" "AV-MIG build input verifier self-tests $avmig_input"
  assert_eq true "$(value_of "$output" security_dotnet)" "AV-MIG build input dependency scan $avmig_input"
done

# Program/AppHub changes are concrete mutation targets and must rerun the
# expensive verifier self-tests before merge.
repo="$tmp_root/avmig-mutation-target"
init_repo "$repo"
mkdir -p "$repo/src/Coglatas.Web/Realtime"
printf 'app.MapHub<AppHub>("/hubs/app");\n' > "$repo/src/Coglatas.Web/Program.cs"
printf '[Authorize] public sealed class AppHub {}\n' > "$repo/src/Coglatas.Web/Realtime/AppHub.cs"
base="$(commit_all "$repo" base)"
printf 'app.MapHub<AppHub>("/hubs/app"); // changed\n' > "$repo/src/Coglatas.Web/Program.cs"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq true "$(value_of "$output" avmig_contract)" "AV-MIG mutation target contract"
assert_eq true "$(value_of "$output" avmig_selftests)" "AV-MIG mutation target self-tests"

# CI infrastructure changes must exercise both the AV-MIG/OpenAPI contract and
# its verifier self-tests. This prevents a CI-only edit from silently disabling
# the contract producer while still claiming verifier coverage.
repo="$tmp_root/ci-infrastructure"
init_repo "$repo"
mkdir -p "$repo/.github/workflows"
printf 'name: CI\n' > "$repo/.github/workflows/ci.yml"
base="$(commit_all "$repo" base)"
printf 'name: CI\n# changed\n' > "$repo/.github/workflows/ci.yml"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq true "$(value_of "$output" avmig_contract)" "CI infrastructure AV-MIG contract"
assert_eq true "$(value_of "$output" avmig_selftests)" "CI infrastructure AV-MIG self-tests"
assert_eq full "$(value_of "$output" backend_test_scope)" "CI infrastructure backend full suite"

# ReSharper/editor configuration changes must route through the shared .NET
# build so the PR ReSharper lane cannot be bypassed by config-only changes.
for config_input in .editorconfig Coglatas.slnx.DotSettings nested/rules.dotsettings; do
  slug="${config_input//\//-}"
  repo="$tmp_root/resharper-config-${slug//./-}"
  init_repo "$repo"
  printf 'base\n' > "$repo/README.md"
  base="$(commit_all "$repo" base)"
  mkdir -p "$(dirname "$repo/$config_input")"
  printf 'changed\n' > "$repo/$config_input"
  head="$(commit_all "$repo" head)"
  output="$(route_repo "$repo" "$base" "$head")"
  assert_eq true "$(value_of "$output" backend)" "ReSharper config backend $config_input"
  assert_eq true "$(value_of "$output" backend_tests)" "ReSharper config tests $config_input"
  assert_eq full "$(value_of "$output" backend_test_scope)" "ReSharper config full scope $config_input"
done

# Main browser artifact workflow changes must keep the browser matrix enabled.
for workflow_input in \
  .github/workflows/main-build-artifacts.yml \
  .github/workflows/compat-critical-preflight.yml \
  .github/workflows/mobile-compatibility.yml; do
  slug="${workflow_input//\//-}"
  repo="$tmp_root/browser-workflow-${slug//./-}"
  init_repo "$repo"
  printf 'base\n' > "$repo/README.md"
  base="$(commit_all "$repo" base)"
  mkdir -p "$(dirname "$repo/$workflow_input")"
  printf 'changed\n' > "$repo/$workflow_input"
  head="$(commit_all "$repo" head)"
  output="$(route_repo "$repo" "$base" "$head")"
  assert_eq true "$(value_of "$output" frontend_playwright)" "browser workflow route $workflow_input"
done

# Cross-cutting Common changes intentionally fail safe to the full backend suite.
repo="$tmp_root/common"
init_repo "$repo" Announcements
mkdir -p "$repo/src/Coglatas.Application/Common"
printf 'public sealed class Clock {}\n' > "$repo/src/Coglatas.Application/Common/Clock.cs"
base="$(commit_all "$repo" base)"
printf 'public sealed class Clock { public int Version => 2; }\n' > "$repo/src/Coglatas.Application/Common/Clock.cs"
head="$(commit_all "$repo" head)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq full "$(value_of "$output" backend_test_scope)" "common fallback"

# A positively identified documentation-only diff is the sole exemption. A
# new/unclassified path and an invalid base cannot suppress a required owner.
repo="$tmp_root/functional-docs"
init_repo "$repo"
printf 'base\n' > "$repo/README.md"
base="$(commit_all "$repo" base)"
printf 'updated prose\n' > "$repo/README.md"
head="$(commit_all "$repo" docs)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq false "$(value_of "$output" functional)" "docs Functional exemption"
assert_eq validated-documentation-only "$(value_of "$output" functional_reason)" "docs routing reason"
base="$head"
mkdir -p "$repo/new-runtime"
printf 'unknown executable input\n' > "$repo/new-runtime/entry"
head="$(commit_all "$repo" runtime)"
output="$(route_repo "$repo" "$base" "$head")"
assert_eq true "$(value_of "$output" functional)" "unknown Functional path"
assert_eq true "$(value_of "$output" backend)" "Functional needs .NET artifact"
assert_eq true "$(value_of "$output" frontend_build)" "Functional needs frontend artifact"
output="$(route_repo "$repo" "1111111111111111111111111111111111111111" "$head")"
assert_eq true "$(value_of "$output" functional)" "invalid base enables Functional"
assert_eq unknown-diff-run-all "$(value_of "$output" functional_reason)" "invalid base reason"

echo "route-main-ci-changes regression tests passed"
