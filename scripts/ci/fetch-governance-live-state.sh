#!/usr/bin/env bash
set -euo pipefail

if [[ "$-" == *x* ]]; then
  echo "Refusing to fetch governance state while shell xtrace is enabled." >&2
  exit 2
fi

repository="${1:-}"
output="${2:-}"
if [[ -z "$repository" || ! "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
  echo "usage: $0 OWNER/REPO OUTPUT.json" >&2
  exit 2
fi
if [[ -z "$output" ]]; then
  echo "output path is required" >&2
  exit 2
fi
: "${GH_TOKEN:?GH_TOKEN is required for authoritative GitHub API reads}"

workdir="$(mktemp -d "${RUNNER_TEMP:-/tmp}/governance-live-state.XXXXXX")"
trap 'rm -rf "$workdir"' EXIT

repo_json="$workdir/repository.json"
branch_json="$workdir/branch.json"
ruleset_pages="$workdir/ruleset-pages.json"
rulesets_json="$workdir/rulesets.json"
classic_json="$workdir/classic-branch-protection.json"

gh api \
  -H 'Accept: application/vnd.github+json' \
  "repos/$repository" \
  > "$repo_json"

default_branch="$(jq -er '.default_branch | select(type == "string" and length > 0)' "$repo_json")"

gh api \
  -H 'Accept: application/vnd.github+json' \
  "repos/$repository/branches/$default_branch" \
  > "$branch_json"

gh api --paginate --slurp \
  -H 'Accept: application/vnd.github+json' \
  "repos/$repository/rulesets?includes_parents=true&per_page=100" \
  > "$ruleset_pages"

: > "$workdir/rulesets.ndjson"
while IFS= read -r ruleset_id; do
  [[ "$ruleset_id" =~ ^[0-9]+$ ]] || {
    echo "invalid ruleset id from GitHub API: $ruleset_id" >&2
    exit 2
  }
  gh api \
    -H 'Accept: application/vnd.github+json' \
    "repos/$repository/rulesets/$ruleset_id?includes_parents=true" \
    >> "$workdir/rulesets.ndjson"
  printf '\n' >> "$workdir/rulesets.ndjson"
done < <(jq -er '.[][] | .id' "$ruleset_pages")

jq -s '.' "$workdir/rulesets.ndjson" > "$rulesets_json"

# Classic branch-protection detail is a supplemental surface. It can require an
# administration-read capability that GITHUB_TOKEN/installed integrations do not have.
# Record observability explicitly instead of silently treating a 403 as "not configured".
classic_raw="$workdir/classic.raw.json"
classic_err="$workdir/classic.err"
set +e
gh api \
  -H 'Accept: application/vnd.github+json' \
  "repos/$repository/branches/$default_branch/protection" \
  > "$classic_raw" 2> "$classic_err"
classic_rc=$?
set -e

if [[ "$classic_rc" -eq 0 ]]; then
  jq '{
    observable: true,
    configured: true,
    http_status: 200,
    error_class: null,
    details: {
      required_status_checks: (
        if .required_status_checks == null then null else {
          strict: (.required_status_checks.strict // null),
          contexts: ((.required_status_checks.contexts // []) | sort),
          checks: ((.required_status_checks.checks // []) | sort_by(.context, .app_id // 0))
        } end
      ),
      enforce_admins: (.enforce_admins.enabled // null),
      required_pull_request_reviews: (
        if .required_pull_request_reviews == null then null else {
          dismiss_stale_reviews: (.required_pull_request_reviews.dismiss_stale_reviews // null),
          require_code_owner_reviews: (.required_pull_request_reviews.require_code_owner_reviews // null),
          required_approving_review_count: (.required_pull_request_reviews.required_approving_review_count // null),
          require_last_push_approval: (.required_pull_request_reviews.require_last_push_approval // null)
        } end
      ),
      required_signatures: (.required_signatures.enabled // null),
      required_linear_history: (.required_linear_history.enabled // null),
      allow_force_pushes: (.allow_force_pushes.enabled // null),
      allow_deletions: (.allow_deletions.enabled // null),
      block_creations: (.block_creations.enabled // null),
      required_conversation_resolution: (.required_conversation_resolution.enabled // null),
      lock_branch: (.lock_branch.enabled // null),
      allow_fork_syncing: (.allow_fork_syncing.enabled // null)
    }
  }' "$classic_raw" > "$classic_json"
else
  http_status="$(grep -oE 'HTTP [0-9]{3}' "$classic_err" | tail -1 | awk '{print $2}' || true)"
  case "$http_status" in
    404)
      jq -n '{observable: true, configured: false, http_status: 404, error_class: "not-configured", details: null}' > "$classic_json"
      ;;
    403)
      jq -n '{observable: false, configured: null, http_status: 403, error_class: "forbidden", details: null}' > "$classic_json"
      ;;
    *)
      jq -n --arg status "${http_status:-unknown}" '{observable: false, configured: null, http_status: (if $status | test("^[0-9]+$") then ($status | tonumber) else null end), error_class: "api-failure", details: null}' > "$classic_json"
      ;;
  esac
fi

mkdir -p "$(dirname "$output")"
tmp_output="$workdir/live-state.json"
jq -n \
  --argjson repository "$(jq '{id, full_name, default_branch}' "$repo_json")" \
  --argjson branch "$(jq '{name, protected, protection}' "$branch_json")" \
  --argjson rulesets "$(cat "$rulesets_json")" \
  --argjson classic "$(cat "$classic_json")" \
  '{
    repository: $repository,
    branch: $branch,
    rulesets: $rulesets,
    classic_branch_protection: $classic
  }' \
  > "$tmp_output"

mv "$tmp_output" "$output"
