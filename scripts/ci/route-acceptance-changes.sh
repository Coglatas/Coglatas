#!/usr/bin/env bash
set -euo pipefail

profile="${1:?usage: route-acceptance-changes.sh <mbj02|mbj03> [base-sha] [head-sha]}"
base_sha="${2:-${BASE_SHA:-}}"
head_sha="${3:-${HEAD_SHA:-${GITHUB_SHA:-HEAD}}}"
zero_sha="0000000000000000000000000000000000000000"

emit() {
  local value="$1"
  local reason="$2"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "run=$value" >> "$GITHUB_OUTPUT"
  else
    echo "run=$value"
  fi
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      echo "### Acceptance change routing"
      echo "- profile: \`$profile\`"
      echo "- run heavy acceptance: \`$value\`"
      echo "- reason: $reason"
    } >> "$GITHUB_STEP_SUMMARY"
  fi
}

case "$profile" in
  mbj02|mbj03) ;;
  *)
    echo "Unsupported acceptance routing profile: $profile" >&2
    exit 2
    ;;
esac

if [[ "${GITHUB_EVENT_NAME:-}" == "workflow_dispatch" ]]; then
  emit true "manual dispatch always runs the complete acceptance workflow"
  exit 0
fi

if [[ -z "$base_sha" || "$base_sha" == "$zero_sha" ]] || \
   ! git cat-file -e "${base_sha}^{commit}" 2>/dev/null || \
   ! git cat-file -e "${head_sha}^{commit}" 2>/dev/null; then
  emit true "safe diff base is unavailable; fail-open to the heavy acceptance workflow"
  exit 0
fi

changed_file_list="${RUNNER_TEMP:-/tmp}/acceptance-${profile}-changed-files.txt"
git diff --name-only "$base_sha" "$head_sha" > "$changed_file_list"

diff_has_relevant_line() {
  local path="$1"
  local regex="$2"
  git diff --unified=0 "$base_sha" "$head_sha" -- "$path" \
    | grep -E '^[+-]' \
    | grep -Ev '^(\+\+\+|---)' \
    | grep -Eiq "$regex"
}

run=false
reason="no changed file or shared-file diff intersects the ${profile} acceptance contract"

while IFS= read -r path; do
  [[ -n "$path" ]] || continue

  case "$profile" in
    mbj02)
      case "$path" in
        .github/workflows/mbj02-invite-onboarding.yml|\
        scripts/ci/route-acceptance-changes.sh|\
        scripts/ci/run-mbj02-invite-acceptance.sh|\
        scripts/ci/verify-npm-lockfile.mjs|\
        tests/ui/mbj02-invite-acceptance.mjs|\
        infra/compose/test/acceptance/mbj02-invite.yml|\
        infra/compose/test/real-backend-smoke.yml|\
        Dockerfile|infra/docker/playwright.Dockerfile|\
        Coglatas.slnx|global.json|Directory.Build.*|Directory.Packages.*|NuGet.config|\
        package.json|package-lock.json|src/*.csproj|\
        src/Coglatas.Application/Admin/*|\
        src/Coglatas.Application/Auth/*|\
        src/Coglatas.Application/Common/*|\
        src/Coglatas.Application/Security/*|\
        src/Coglatas.Application/Tenancy/*|\
        src/Coglatas.Application/Workspaces/*|\
        src/Coglatas.Domain/Common/*|\
        src/Coglatas.Domain/Entities/IdentityEntities.cs|\
        src/Coglatas.Infrastructure/Persistence/AdminRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/AuthRepositories.cs|\
        src/Coglatas.Infrastructure/Persistence/CapabilityGrantRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/TenantRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/WorkspaceRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/Configurations/TenantConfigurations.cs|\
        src/Coglatas.Infrastructure/Security/*|\
        src/Coglatas.Web/Controllers/AdminController.cs|\
        src/Coglatas.Web/Controllers/AuthController.cs|\
        src/Coglatas.Web/Controllers/InvitesController.cs|\
        src/Coglatas.Web/Controllers/SecurityController.cs|\
        src/Coglatas.Web/Program.cs|\
        src/Coglatas.Web/Security/*|\
        src/Coglatas.Web/Tenancy/*|\
        src/Coglatas.Web/appsettings*.json)
          run=true
          reason="direct MBJ-02 dependency changed: \`$path\`"
          break
          ;;
        src/Coglatas.Application/DependencyInjection.cs|\
        src/Coglatas.Infrastructure/DependencyInjection.cs|\
        src/Coglatas.Infrastructure/Persistence/AppDbContext.cs|\
        src/Coglatas.Infrastructure/Persistence/Configurations/ProductionConfigurations.cs|\
        src/Coglatas.Infrastructure/Persistence/Migrations/*|\
        src/Coglatas.Domain/Entities/ProductionEntities.cs|\
        src/Coglatas.Domain/Enums/*)
          if diff_has_relevant_line "$path" '(Admin|Auth|Invite|Session|User|Tenant|Workspace|Membership|CapabilityGrant|Security|Role)'; then
            run=true
            reason="shared file changed MBJ-02-relevant symbols: \`$path\`"
            break
          fi
          ;;
      esac
      ;;
    mbj03)
      case "$path" in
        .github/workflows/mbj03-session-lifecycle.yml|\
        scripts/ci/route-acceptance-changes.sh|\
        scripts/ci/run-mbj03-session-acceptance.sh|\
        scripts/ci/verify-npm-lockfile.mjs|\
        tests/ui/mbj03-session-acceptance.mjs|\
        infra/compose/test/acceptance/mbj03-session.yml|\
        infra/compose/test/real-backend-smoke.yml|\
        Dockerfile|infra/docker/playwright.Dockerfile|\
        Coglatas.slnx|global.json|Directory.Build.*|Directory.Packages.*|NuGet.config|\
        package.json|package-lock.json|src/*.csproj|\
        src/Coglatas.Application/Admin/*|\
        src/Coglatas.Application/Auth/*|\
        src/Coglatas.Application/Common/*|\
        src/Coglatas.Application/Realtime/*|\
        src/Coglatas.Application/Security/*|\
        src/Coglatas.Application/Tenancy/*|\
        src/Coglatas.Application/Workspaces/*|\
        src/Coglatas.Domain/Common/*|\
        src/Coglatas.Domain/Entities/IdentityEntities.cs|\
        src/Coglatas.Infrastructure/Persistence/AdminRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/AuthRepositories.cs|\
        src/Coglatas.Infrastructure/Persistence/CapabilityGrantRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/TenantRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/WorkspaceRepository.cs|\
        src/Coglatas.Infrastructure/Persistence/Configurations/TenantConfigurations.cs|\
        src/Coglatas.Infrastructure/Security/*|\
        src/Coglatas.Web/Controllers/AdminController.cs|\
        src/Coglatas.Web/Controllers/AuthController.cs|\
        src/Coglatas.Web/Controllers/InvitesController.cs|\
        src/Coglatas.Web/Controllers/SecurityController.cs|\
        src/Coglatas.Web/Hubs/*|\
        src/Coglatas.Web/Program.cs|\
        src/Coglatas.Web/Security/*|\
        src/Coglatas.Web/Tenancy/*|\
        src/Coglatas.Web/appsettings*.json)
          run=true
          reason="direct MBJ-03 dependency changed: \`$path\`"
          break
          ;;
        src/Coglatas.Application/DependencyInjection.cs|\
        src/Coglatas.Infrastructure/DependencyInjection.cs|\
        src/Coglatas.Infrastructure/Persistence/AppDbContext.cs|\
        src/Coglatas.Infrastructure/Persistence/Configurations/ProductionConfigurations.cs|\
        src/Coglatas.Infrastructure/Persistence/Migrations/*|\
        src/Coglatas.Domain/Entities/ProductionEntities.cs|\
        src/Coglatas.Domain/Enums/*)
          if diff_has_relevant_line "$path" '(Admin|Auth|Invite|Session|User|Tenant|Workspace|Membership|CapabilityGrant|Security|Role|Realtime|Hub|Csrf|Cookie)'; then
            run=true
            reason="shared file changed MBJ-03-relevant symbols: \`$path\`"
            break
          fi
          ;;
      esac
      ;;
  esac
done < "$changed_file_list"

emit "$run" "$reason"
