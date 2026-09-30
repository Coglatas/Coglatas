#!/usr/bin/env bash
set -Eeuo pipefail

zero_sha="0000000000000000000000000000000000000000"
base_sha="${BASE_SHA:-}"
head_sha="${HEAD_SHA:-${GITHUB_SHA:-}}"
output_file="${GITHUB_OUTPUT:-}"
summary_file="${GITHUB_STEP_SUMMARY:-}"

keys=(
  backend
  backend_ef
  backend_tests
  backend_pr07b
  backend_pr07c
  backend_pr07d
  frontend
  frontend_build
  frontend_unit
  frontend_architecture
  frontend_license_guard
  frontend_storybook
  frontend_playwright
  avmig_contract
  security
  security_dotnet
  security_compose
  security_migration
  security_image
)

write_output() {
  local key="$1"
  local value="$2"
  if [[ -n "$output_file" ]]; then
    printf '%s=%s\n' "$key" "$value" >> "$output_file"
  else
    printf '%s=%s\n' "$key" "$value"
  fi
}

route_all() {
  local key
  for key in "${keys[@]}"; do
    write_output "$key" "true"
  done
  write_output "backend_test_scope" "full"
  write_output "backend_test_filter" ""
  write_output "frontend_unit_scope" "full"
  write_output "frontend_unit_features" ""
  if [[ -n "$summary_file" ]]; then
    echo "Unable to establish a safe diff base; all CI work is enabled." >> "$summary_file"
  fi
}

if [[ -z "$base_sha" || "$base_sha" == "$zero_sha" || -z "$head_sha" ]] || \
   ! git cat-file -e "${base_sha}^{commit}" 2>/dev/null || \
   ! git cat-file -e "${head_sha}^{commit}" 2>/dev/null; then
  route_all
  exit 0
fi

changed_file_list="${RUNNER_TEMP:-/tmp}/ci-changed-files.txt"
git diff --name-only "$base_sha" "$head_sha" > "$changed_file_list"

backend=false
backend_ef=false
backend_tests=false
backend_test_full=false
backend_pr07b=false
backend_pr07c=false
backend_pr07d=false
backend_scopes=()
frontend=false
frontend_build=false
frontend_unit=false
frontend_unit_full=false
frontend_architecture=false
frontend_license_guard=false
frontend_storybook=false
frontend_playwright=false
avmig_contract=false
security=false
security_dotnet=false
security_compose=false
security_migration=false
security_image=false
frontend_features=()

add_unique() {
  local value="$1"
  shift
  local existing
  for existing in "$@"; do
    [[ "$existing" == "$value" ]] && return 1
  done
  return 0
}

add_backend_scope() {
  local scope="$1"
  backend=true
  backend_tests=true

  if [[ "$scope" == "PostgreSql" ]]; then
    backend_ef=true
  fi

  if [[ "$backend_test_full" == "true" ]]; then
    return 0
  fi

  if [[ ! -d "tests/Coglatas.Tests/$scope" ]]; then
    backend_test_full=true
    return 0
  fi

  if add_unique "$scope" "${backend_scopes[@]:-}"; then
    backend_scopes+=("$scope")
  fi
}

mark_backend_full() {
  backend=true
  backend_ef=true
  backend_tests=true
  backend_test_full=true
}

mark_backend_scope_name() {
  local name="$1"
  case "$name" in
    Communication|Messaging)
      add_backend_scope "Messaging"
      ;;
    Planning|Projects|TaskExecution)
      add_backend_scope "Projects"
      if [[ -d tests/Coglatas.Tests/Performance ]]; then
        add_backend_scope "Performance"
      fi
      ;;
    UiShell)
      if [[ -d tests/Coglatas.Tests/UiShell ]]; then
        add_backend_scope "UiShell"
      else
        add_backend_scope "Workspaces"
      fi
      ;;
    *)
      add_backend_scope "$name"
      ;;
  esac
}

mark_pr07_from_text() {
  local text="$1"

  if grep -Eiq '(TaskSubresource|TaskComment|CommentAuthor|Mention|Assignee|Reviewer|Collaborator|Assignment|ImportantOnly|SignificanceSafety|RelationshipTarget)' <<< "$text"; then
    backend_pr07b=true
  fi

  if grep -Eiq '(DeadlineDigest|TaskDeadline|TaskWatch|WatchRepository|DigestCandidate|NotificationSchedule|ScheduledDiagnostic|WorkspaceTimeZone|CommitFence)' <<< "$text"; then
    backend_pr07c=true
  fi

  if grep -Eiq '(AuthorizedDelivery|NotificationCreated|NotificationReadState|OpenTaskNotification|OpenDigestNotification|TaskInvalidation|OutboxDispatcher|OutboxReplay|ArtifactNotification|ProjectMessageNotification|Realtime.*Notification)' <<< "$text"; then
    backend_pr07d=true
  fi
}

mark_backend_content_domains() {
  local text="$1"
  local matched=false

  if grep -Eiq '(Announcement|DistributionTarget)' <<< "$text"; then
    add_backend_scope "Announcements"
    matched=true
  fi
  if grep -Eiq '(ArtifactFinding|ArtifactClaim|AuditEvent|AuditLog|Evidence|SourceProvenance)' <<< "$text"; then
    add_backend_scope "Audit"
    matched=true
  fi
  if grep -Eiq '(FileObject|FileFolder|FileGrant|FileStorage|FileAssociation)' <<< "$text"; then
    add_backend_scope "Files"
    matched=true
  fi
  if grep -Eiq '(Conversation|DirectMessage|ChannelMessage|MessageFollowUp|MessageNotification)' <<< "$text"; then
    add_backend_scope "Messaging"
    matched=true
  fi
  if grep -Eiq '(Notification|Outbox|DeadlineDigest)' <<< "$text"; then
    add_backend_scope "Notifications"
    matched=true
  fi
  if grep -Eiq '(Project|Task|Planning|Kanban|Gantt|WorkItem|Milestone)' <<< "$text"; then
    add_backend_scope "Projects"
    matched=true
  fi
  if grep -Eiq '(Workspace|CapabilityGrant)' <<< "$text"; then
    add_backend_scope "Workspaces"
    matched=true
  fi

  mark_pr07_from_text "$text"

  if grep -Eiq '(Invite|Session|Authentication|Authorization|CurrentTenant|TenantUser|SystemRole|Security|Csrf|UserRepository)' <<< "$text"; then
    mark_backend_full
    matched=true
  fi

  if [[ "$matched" != "true" ]]; then
    mark_backend_full
  fi
}

changed_lines_for() {
  local path="$1"
  git diff --unified=0 "$base_sha" "$head_sha" -- "$path" \
    | sed -n '/^[+-][^+-]/s/^[+-]//p'
}

mark_backend_file_by_name() {
  local path="$1"
  local name
  name="$(basename "$path")"
  local matched=false

  case "$name" in
    *Announcement*) add_backend_scope "Announcements"; matched=true ;;
  esac
  case "$name" in
    *Audit*|*Artifact*|*Evidence*) add_backend_scope "Audit"; matched=true ;;
  esac
  case "$name" in
    *File*) add_backend_scope "Files"; matched=true ;;
  esac
  case "$name" in
    *Conversation*|*Message*) add_backend_scope "Messaging"; matched=true ;;
  esac
  case "$name" in
    *Notification*|*Outbox*|*Digest*) add_backend_scope "Notifications"; matched=true ;;
  esac
  case "$name" in
    *Project*|*Task*|*Planning*|*Kanban*|*Gantt*) add_backend_scope "Projects"; matched=true ;;
  esac
  case "$name" in
    *Workspace*) add_backend_scope "Workspaces"; matched=true ;;
  esac

  mark_pr07_from_text "$path"

  case "$name" in
    *Auth*|*Invite*|*Session*|*Tenant*|*Security*|*User*|*CapabilityGrant*)
      mark_backend_full
      matched=true
      ;;
  esac

  if [[ "$matched" != "true" ]]; then
    mark_backend_content_domains "$(changed_lines_for "$path")"
  fi
}

mark_backend_test_file() {
  local path="$1"
  backend=true

  if [[ "$path" =~ ^tests/Coglatas\.Tests/([^/]+)/ ]]; then
    local scope="${BASH_REMATCH[1]}"
    if [[ "$scope" == "PostgreSql" ]]; then
      # PostgreSQL-scoped tests use the CI service database and require the schema
      # even when the production EF model itself was not changed.
      backend_ef=true
    fi
    if [[ "$scope" == "Support" ]]; then
      mark_backend_full
    else
      add_backend_scope "$scope"
    fi
  else
    mark_backend_full
  fi

  local trait_text=""
  if [[ -f "$path" ]]; then
    trait_text="$(grep -E 'TaskV1PR07[B-D]' "$path" 2>/dev/null || true)"
  fi
  trait_text+=$'\n'"$(changed_lines_for "$path")"
  [[ "$trait_text" == *TaskV1PR07B* ]] && backend_pr07b=true
  [[ "$trait_text" == *TaskV1PR07C* ]] && backend_pr07c=true
  [[ "$trait_text" == *TaskV1PR07D* ]] && backend_pr07d=true
  return 0
}

add_frontend_feature() {
  local feature="$1"
  if add_unique "$feature" "${frontend_features[@]:-}"; then
    frontend_features+=("$feature")
  fi
}

mark_unit_for_path() {
  local path="$1"
  if [[ "$path" =~ ^frontend/src/app/features/([^/]+)/ ]]; then
    local feature="${BASH_REMATCH[1]}"
    if find "frontend/src/app/features/$feature" -type f -name '*.spec.ts' -print -quit 2>/dev/null | grep -q .; then
      frontend_unit=true
      add_frontend_feature "$feature"
    fi
  else
    frontend_unit=true
    frontend_unit_full=true
  fi
}

mark_frontend_runtime() {
  local path="$1"
  frontend=true
  frontend_build=true
  frontend_playwright=true

  case "$path" in
    *.ts|*.html)
      mark_unit_for_path "$path"
      ;;
  esac

  case "$path" in
    *.ts)
      frontend_architecture=true
      ;;
  esac

  if [[ "$path" == frontend/src/app/shared/* || "$path" == frontend/src/app/core/* || "$path" == frontend/src/styles.* ]]; then
    frontend_storybook=true
  else
    local dir
    dir="$(dirname "$path")"
    if find "$dir" -maxdepth 1 -type f -name '*.stories.ts' -print -quit 2>/dev/null | grep -q .; then
      frontend_storybook=true
    fi
  fi
}

while IFS= read -r path; do
  [[ -n "$path" ]] || continue

  case "$path" in
    .github/workflows/ci.yml|scripts/ci/route-main-ci-changes.sh|scripts/ci/require-needs-success.py|scripts/ci/validate-frontend-ci-helpers.sh)
      backend=true
      backend_ef=true
      backend_tests=true
      backend_test_full=true
      backend_pr07b=true
      backend_pr07c=true
      backend_pr07d=true
      frontend=true
      frontend_build=true
      frontend_unit=true
      frontend_unit_full=true
      frontend_architecture=true
      frontend_license_guard=true
      frontend_storybook=true
      frontend_playwright=true
      security=true
      security_dotnet=true
      security_compose=true
      security_migration=true
      security_image=true
      continue
      ;;
  esac

  # Backend compile/test routing.
  case "$path" in
    Coglatas.slnx|global.json|NuGet.config|Directory.Build.*|Directory.Packages.*|.config/*|tests/Coglatas.Tests/Coglatas.Tests.csproj|src/*.csproj)
      mark_backend_full
      backend_ef=true
      ;;
    scripts/ci/configure-persistent-caches.sh|scripts/ci/verify-trx-results.sh)
      mark_backend_full
      ;;
    scripts/ci/task-pr07b-required-tests.txt)
      backend=true
      backend_pr07b=true
      ;;
    scripts/ci/task-pr07c-required-tests.txt)
      backend=true
      backend_pr07c=true
      ;;
    scripts/ci/task-pr07d-required-tests.txt)
      backend=true
      backend_pr07d=true
      ;;
    tests/Coglatas.Tests/*)
      mark_backend_test_file "$path"
      ;;
    src/Coglatas.Application/DependencyInjection.cs|src/Coglatas.Application/Common/*|src/Coglatas.Application/Security/*|src/Coglatas.Application/Tenancy/*)
      mark_backend_full
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Application/*)
      backend=true
      if [[ "$path" =~ ^src/Coglatas\.Application/([^/]+)/ ]]; then
        mark_backend_scope_name "${BASH_REMATCH[1]}"
      else
        mark_backend_full
      fi
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Domain/Entities/IdentityEntities.cs|src/Coglatas.Domain/Common/*)
      mark_backend_full
      backend_ef=true
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Domain/Entities/MessagingEntities.cs|src/Coglatas.Domain/Entities/CommunicationEntities.cs)
      backend=true
      backend_ef=true
      add_backend_scope "Messaging"
      add_backend_scope "PostgreSql"
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Domain/Entities/*|src/Coglatas.Domain/Enums/*)
      backend=true
      backend_ef=true
      add_backend_scope "PostgreSql"
      mark_backend_content_domains "$(changed_lines_for "$path")"
      ;;
    src/Coglatas.Infrastructure/Persistence/Migrations/*)
      backend=true
      backend_ef=true
      add_backend_scope "PostgreSql"
      mark_backend_file_by_name "$path"
      ;;
    src/Coglatas.Infrastructure/Persistence/AppDbContext.cs|src/Coglatas.Infrastructure/Persistence/Configurations/*)
      backend=true
      backend_ef=true
      add_backend_scope "PostgreSql"
      mark_backend_content_domains "$(changed_lines_for "$path")"
      ;;
    src/Coglatas.Infrastructure/DependencyInjection.cs)
      backend=true
      mark_backend_content_domains "$(changed_lines_for "$path")"
      ;;
    src/Coglatas.Infrastructure/Persistence/*)
      backend=true
      add_backend_scope "PostgreSql"
      mark_backend_file_by_name "$path"
      ;;
    src/Coglatas.Infrastructure/Files/*|src/Coglatas.Infrastructure/FileStorage/*)
      add_backend_scope "Files"
      ;;
    src/Coglatas.Infrastructure/BackgroundJobs/*)
      add_backend_scope "Notifications"
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Infrastructure/TaskExecution/*)
      add_backend_scope "Projects"
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Infrastructure/Security/*)
      mark_backend_full
      ;;
    src/Coglatas.Infrastructure/*)
      mark_backend_full
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Web/Controllers/AuthController.cs|src/Coglatas.Web/Controllers/InvitesController.cs|src/Coglatas.Web/Controllers/SecurityController.cs|src/Coglatas.Web/Controllers/AdminController.cs|src/Coglatas.Web/Program.cs|src/Coglatas.Web/Security/*|src/Coglatas.Web/Tenancy/*)
      mark_backend_full
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Web/Controllers/*)
      backend=true
      mark_backend_file_by_name "$path"
      ;;
    src/Coglatas.Web/Hubs/*)
      add_backend_scope "Realtime"
      add_backend_scope "Messaging"
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
    src/Coglatas.Web/*)
      mark_backend_full
      mark_pr07_from_text "$path $(changed_lines_for "$path")"
      ;;
  esac

  # Frontend routing.
  case "$path" in
    frontend/package.json|frontend/package-lock.json|frontend/.npmrc|frontend/angular.json|frontend/tsconfig*.json)
      frontend=true
      frontend_build=true
      frontend_unit=true
      frontend_unit_full=true
      frontend_architecture=true
      frontend_license_guard=true
      frontend_storybook=true
      frontend_playwright=true
      ;;
    frontend/.storybook/*)
      frontend=true
      frontend_storybook=true
      ;;
    frontend/scripts/check-architecture.mjs|frontend/scripts/check-architecture.node-test.mjs)
      frontend=true
      frontend_architecture=true
      ;;
    frontend/scripts/require-syncfusion-license.mjs|frontend/scripts/require-syncfusion-license.node-test.mjs|frontend/scripts/sanitize-syncfusion-theme-css.mjs|frontend/scripts/sanitize-syncfusion-theme-css.node-test.mjs)
      frontend=true
      frontend_build=true
      frontend_license_guard=true
      frontend_storybook=true
      ;;
    frontend/scripts/*)
      frontend=true
      frontend_build=true
      frontend_architecture=true
      frontend_storybook=true
      frontend_playwright=true
      ;;
    frontend/src/*.spec.ts)
      frontend=true
      mark_unit_for_path "$path"
      ;;
    frontend/src/*.stories.ts)
      frontend=true
      frontend_storybook=true
      ;;
    frontend/src/*)
      mark_frontend_runtime "$path"
      ;;
    frontend/*)
      frontend=true
      frontend_build=true
      frontend_unit=true
      frontend_unit_full=true
      frontend_architecture=true
      frontend_license_guard=true
      frontend_storybook=true
      frontend_playwright=true
      ;;
    package.json|package-lock.json|.npmrc)
      frontend=true
      frontend_playwright=true
      ;;
    Dockerfile.playwright|docker-compose.playwright.yml|playwright.config.*|scripts/ci/npm-ci-retry.sh)
      frontend=true
      frontend_playwright=true
      ;;
    tests/ui/*real-backend*|tests/ui/mbj*|tests/ui/run-mbj*|tests/ui/*public-https*|tests/ui/*u22*|tests/ui/wpc*)
      ;;
    tests/ui/angular-smoke.spec.ts|tests/ui/message-mobile-navigation.spec.ts|tests/ui/message-search-filters.spec.ts|tests/ui/message-actions.spec.ts|tests/ui/audit-claims-evidence.spec.ts|tests/ui/message-thread-context.spec.ts|tests/ui/message-follow-ups.spec.ts|tests/ui/app.spec.ts|tests/ui/run-angular-playwright.mjs|tests/ui/run-angular-playwright-compose.mjs|tests/ui/serve-static.mjs|tests/ui/*)
      frontend=true
      frontend_playwright=true
      ;;
  esac

  # AV-MIG contract routing.
  case "$path" in
    global.json|NuGet.config|Directory.Build.*|src/*.csproj|docs/migration/avalonia/*|scripts/ci/*av_mig*|scripts/ci/generate-security-openapi-contract.sh|src/Coglatas.Web/*|src/Coglatas.Application/*|tests/Coglatas.Tests/OpenApi/*|tools/AvMig.SourceInspector/*)
      avmig_contract=true
      security=true
      security_dotnet=true
      ;;
  esac

  # Security routing.
  # Runtime/API implementation changes must execute the authenticated SEC-03/04/05/06
  # Compose gate on pull requests as well as main. Otherwise contract and fuzz
  # regressions are discovered only after merge.
  case "$path" in
    src/Coglatas.Application/*|src/Coglatas.Domain/*|src/Coglatas.Infrastructure/*|src/Coglatas.Web/*|scripts/security/*|scripts/ci/run-security-runtime-smoke.sh|scripts/ci/generate-security-openapi-contract.sh)
      security=true
      security_dotnet=true
      security_compose=true
      ;;
  esac

  case "$path" in
    Coglatas.slnx|global.json|NuGet.config|Directory.Build.*|Directory.Packages.*|.config/*|src/*.csproj|tests/Coglatas.Tests/*.csproj)
      security=true
      security_dotnet=true
      ;;
  esac

  case "$path" in
    .env|.env.*|.env.example|docker-compose*.yml|deploy/*|scripts/ci/verify-onprem-proxy-topology.sh)
      security=true
      security_compose=true
      ;;
  esac

  case "$path" in
    src/Coglatas.Infrastructure/Migrations/*|docker-compose.onprem.yml|docker-compose.onprem.ci.yml|global.json|NuGet.config|Directory.Build.*|Directory.Packages.*|.config/*|src/*.csproj)
      security=true
      security_migration=true
      ;;
  esac

  case "$path" in
    .dockerignore|.env|.env.*|.env.example|Dockerfile|Dockerfile.*|frontend/package.json|frontend/package-lock.json|frontend/.npmrc|global.json|NuGet.config|Directory.Build.*|Directory.Packages.*|src/*.csproj|*/syncfusion-license.txt|syncfusion-license.txt)
      security=true
      security_image=true
      ;;
  esac
done < "$changed_file_list"

backend_test_scope="none"
backend_test_filter=""
if [[ "$backend_tests" == "true" ]]; then
  if [[ "$backend_test_full" == "true" || "${#backend_scopes[@]}" -eq 0 ]]; then
    backend_test_scope="full"
  else
    backend_test_scope="scoped"
    filters=()
    for scope in "${backend_scopes[@]}"; do
      filters+=("FullyQualifiedName~Coglatas.Tests.${scope}")
    done
    backend_test_filter="$(IFS='|'; echo "${filters[*]}")"
  fi
fi

frontend_unit_scope="none"
frontend_unit_features=""
if [[ "$frontend_unit" == "true" ]]; then
  if [[ "$frontend_unit_full" == "true" || "${#frontend_features[@]}" -eq 0 ]]; then
    frontend_unit_scope="full"
  else
    frontend_unit_scope="features"
    frontend_unit_features="$(IFS=,; echo "${frontend_features[*]}")"
  fi
fi

for key in "${keys[@]}"; do
  write_output "$key" "${!key}"
done
write_output "backend_test_scope" "$backend_test_scope"
write_output "backend_test_filter" "$backend_test_filter"
write_output "frontend_unit_scope" "$frontend_unit_scope"
write_output "frontend_unit_features" "$frontend_unit_features"

if [[ -n "$summary_file" ]]; then
  {
    echo "### CI routing"
    echo "- backend: $backend"
    echo "  - EF migration/model validation: $backend_ef"
    echo "  - main tests: $backend_tests ($backend_test_scope${backend_test_filter:+: $backend_test_filter})"
    echo "  - TASK-V1-PR07-B: $backend_pr07b"
    echo "  - TASK-V1-PR07-C: $backend_pr07c"
    echo "  - TASK-V1-PR07-D: $backend_pr07d"
    echo "- frontend: $frontend"
    echo "  - build: $frontend_build"
    echo "  - unit: $frontend_unit ($frontend_unit_scope${frontend_unit_features:+: $frontend_unit_features})"
    echo "  - architecture: $frontend_architecture"
    echo "  - license guard: $frontend_license_guard"
    echo "  - Storybook: $frontend_storybook"
    echo "  - Playwright: $frontend_playwright"
    echo "- AV-MIG contract: $avmig_contract"
    echo "- security: $security"
    echo "  - .NET dependency scan: $security_dotnet"
    echo "  - Compose validation: $security_compose"
    echo "  - migration smoke: $security_migration"
    echo "  - image/Trivy: $security_image"
    echo
    echo "Changed files:"
    sed 's/^/- `/' "$changed_file_list" | sed 's/$/`/'
  } >> "$summary_file"
fi