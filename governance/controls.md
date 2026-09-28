# Governance control matrix

> Generated view. The source of truth is `governance/policy.json`; do not edit this matrix as policy.
> `scripts/ci/validate-governance-policy.py` fails if this file drifts from the machine-readable contract.

## Policy semantics

- Policy ID: `COGLATAS-GOVERNANCE` v1
- Repository: `NYGsatoshi/Coglatas`
- Default branch: `main`
- Live GitHub state is evidence, **not** the baseline.
- Unknown controls/fields are rejected; invalid policy blocks.
- Critical controls have no implicit permissive defaults.
- Enforcement downgrades must appear as explicit reviewed policy diffs.

## Control matrix

| ID | Family | Scope | Enforcement | Owner | Evidence | Exception | Title |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `GOV-RULESET-001` | `ruleset` | `default-branch`, `scheduled` | `blocking` | `@NYGsatoshi` | `github-live-settings` | forbidden | Default branch protection and ruleset contract |
| `GOV-CHECKS-001` | `required-status-checks` | `pr`, `default-branch` | `blocking` | `@NYGsatoshi` | `status-context` | forbidden | Required pull-request status checks |
| `GOV-SIGNATURE-001` | `required-signatures` | `default-branch` | `blocking` | `@NYGsatoshi` | `github-live-settings` | forbidden | Required commit signatures |
| `GOV-REVIEW-001` | `pr-review-codeowners` | `pr`, `default-branch` | `blocking` | `@NYGsatoshi` | `github-live-settings` | forbidden | Pull request review and CODEOWNERS contract |
| `GOV-BYPASS-001` | `bypass-actors` | `default-branch`, `scheduled` | `blocking` | `@NYGsatoshi` | `github-live-settings` | forbidden | Bypass actor policy |
| `GOV-WORKFLOW-PERM-001` | `workflow-permissions` | `pr`, `scheduled` | `blocking` | `@NYGsatoshi` | `workflow-static` | forbidden | Workflow token permission floor |
| `GOV-TRUST-001` | `workflow-trust-boundary` | `pr` | `blocking` | `@NYGsatoshi` | `workflow-static` | forbidden | Untrusted pull-request workflow boundary |
| `GOV-RUNNER-001` | `self-hosted-runner` | `pr` | `blocking` | `@NYGsatoshi` | `workflow-static` | forbidden | Self-hosted runner eligibility |
| `GOV-ACTION-REF-001` | `action-refs` | `pr`, `scheduled` | `required-on-sensitive-change` | `@NYGsatoshi` | `workflow-static` | allowed via `bounded` | GitHub Action and reusable workflow reference policy |
| `GOV-SENSITIVE-PATH-001` | `governance-sensitive-paths` | `pr` | `blocking` | `@NYGsatoshi` | `repository-file` | forbidden | Governance-sensitive path classification |
| `GOV-WAIVER-001` | `waiver-expiry` | `pr`, `scheduled` | `blocking` | `@NYGsatoshi` | `repository-file` | forbidden | Bounded governance waiver contract |
| `GOV-EVIDENCE-001` | `governance-evidence` | `pr`, `release`, `scheduled` | `blocking` | `@NYGsatoshi` | `governance-manifest` | forbidden | Exact-SHA governance evidence and aggregate gate |

## Expected values

### `GOV-RULESET-001` — Default branch protection and ruleset contract

Keep the protected default-branch rulesets explicit and reviewable instead of learning policy from live GitHub state.

```json
{
  "branch_deletion_allowed": false,
  "non_fast_forward_allowed": false,
  "ruleset_names": [
    "Public Main Protection - Strict External Review",
    "PRreview"
  ],
  "strict_required_status_checks": true,
  "target": "default-branch"
}
```

### `GOV-CHECKS-001` — Required pull-request status checks

Pin every required status context, including trusted evaluator statuses, so rename, skip, missing, or stale states fail closed from one repository-owned contract.

```json
{
  "missing_or_stale": "fail",
  "required": [
    {
      "context": "External PR approval policy",
      "job": "evaluate",
      "kind": "commit-status",
      "workflow": ".github/workflows/external-pr-approval-evaluator.yml"
    },
    {
      "context": "build-test",
      "job": "build-test",
      "kind": "workflow-job",
      "workflow": ".github/workflows/ci.yml"
    },
    {
      "context": "frontend-test",
      "job": "frontend-test",
      "kind": "workflow-job",
      "workflow": ".github/workflows/ci.yml"
    },
    {
      "context": "security-scan",
      "job": "security-scan",
      "kind": "workflow-job",
      "workflow": ".github/workflows/ci.yml"
    },
    {
      "context": "publication-readiness",
      "job": "publication-readiness",
      "kind": "workflow-job",
      "workflow": ".github/workflows/publication-readiness.yml"
    }
  ],
  "strict": true
}
```

### `GOV-SIGNATURE-001` — Required commit signatures

Require signed commits on the protected default branch.

```json
{
  "required": true
}
```

### `GOV-REVIEW-001` — Pull request review and CODEOWNERS contract

Require current-head CODEOWNER approval for external pull requests while keeping owner-authored changes mergeable without bypass in the current single-maintainer topology; native rules enforce PR/thread semantics and the trusted evaluator owns authoritative approval state.

```json
{
  "approvals_required": 0,
  "author_cannot_self_approve": true,
  "changes_requested_blocks": true,
  "codeowners_fallback_required": true,
  "codeowners_required_explicit_patterns": [
    "/.github/",
    "/scripts/ci/",
    "/governance/",
    "/.npmrc",
    "/global.json",
    "/package.json",
    "/package-lock.json",
    "/Dockerfile",
    "/docker-compose.yml",
    "/deploy/",
    "/COPYRIGHT.md",
    "/CONTRIBUTING.md",
    "/THIRD_PARTY_NOTICES.md",
    "/.github/SECURITY.md",
    "/docs/SECURITY.md",
    "/docs/SECURITY_MODEL.md"
  ],
  "codeowners_review_required": false,
  "codeowners_sensitive_paths": [
    ".github/CODEOWNERS",
    ".github/workflows/ci.yml",
    ".github/workflows/publication-readiness.yml",
    ".github/workflows/external-pr-approval-evaluator.yml",
    "scripts/ci/check-governance-review-status-invariants.py",
    "scripts/ci/evaluate-governance-pr-review.py",
    "governance/policy.json",
    "governance/policy.schema.json",
    "global.json",
    "package.json",
    "package-lock.json",
    ".npmrc",
    "Dockerfile",
    "docker-compose.yml",
    "deploy/gcp/README.md",
    "COPYRIGHT.md",
    "CONTRIBUTING.md",
    "THIRD_PARTY_NOTICES.md",
    ".github/SECURITY.md",
    "docs/SECURITY.md",
    "docs/SECURITY_MODEL.md"
  ],
  "dismiss_stale_reviews_on_push": false,
  "draft_pr_blocks": true,
  "external_approval_reviewer_must_be_codeowner": true,
  "external_pr_approval_reviewer": "@NYGsatoshi",
  "external_pr_current_head_approval_required": true,
  "minimum_distinct_codeowners": 1,
  "owner_authored_pr_external_approval_required": false,
  "previous_head_approval_satisfies": false,
  "pull_request_required": true,
  "require_last_push_approval": false,
  "required_review_thread_resolution": true,
  "unattributed_changes_require_additional_approval": false
}
```

### `GOV-BYPASS-001` — Bypass actor policy

Treat bypass as an exceptional administrative capability, never as the normal development path. Every permitted actor must be explicitly identified and justified; the strict baseline currently permits none.

```json
{
  "allowed_actors": [],
  "break_glass_usage_evidence_required": true,
  "normal_development_bypass_forbidden": true,
  "user_always_bypass": "forbidden"
}
```

### `GOV-WORKFLOW-PERM-001` — Workflow token permission floor

Default workflows to read-only and make every write-capable workflow an explicit allowlisted exception.

```json
{
  "default": "read-only",
  "persist_credentials": false,
  "write_permissions_allowlist": [
    {
      "permissions": [
        "contents:write",
        "statuses:write"
      ],
      "workflow": ".github/workflows/external-pr-approval-evaluator.yml"
    }
  ]
}
```

### `GOV-TRUST-001` — Untrusted pull-request workflow boundary

Prevent untrusted PR code from acquiring write tokens, repository secrets, or trusted workflow_run execution.

```json
{
  "pull_request_target": "forbidden",
  "untrusted_pr_secrets": "forbidden",
  "untrusted_pr_write_token": "forbidden",
  "workflow_run_untrusted_head_execution": "forbidden"
}
```

### `GOV-RUNNER-001` — Self-hosted runner eligibility

Keep untrusted PR workloads away from persistent privileged self-hosted runners.

```json
{
  "persistent_privileged": "forbidden",
  "untrusted_pr": "forbidden"
}
```

### `GOV-ACTION-REF-001` — GitHub Action and reusable workflow reference policy

Require immutable third-party references on governance-sensitive execution paths.

```json
{
  "mutable_tag": "forbidden",
  "reusable_workflow": "immutable-sha-or-local",
  "third_party": "immutable-sha"
}
```

### `GOV-SENSITIVE-PATH-001` — Governance-sensitive path classification

Classify governance controls as sensitive so selective CI routing cannot silently skip stronger review and validation.

```json
{
  "catch_all": "forbidden",
  "patterns": [
    ".github/**",
    "scripts/ci/**",
    "governance/**"
  ],
  "require_nonempty_match": true
}
```

### `GOV-WAIVER-001` — Bounded governance waiver contract

Make every temporary governance exception owned, justified, tracked, and automatically expiring.

```json
{
  "rule_name": "bounded"
}
```

### `GOV-EVIDENCE-001` — Exact-SHA governance evidence and aggregate gate

Bind governance evidence and the final ci/governance decision to the exact commit being merged or released.

```json
{
  "aggregate_context": "ci/governance",
  "bind_to": "exact-head-sha",
  "live_evaluator": "trusted-default-branch",
  "missing_stale_skipped_cancelled": "fail"
}
```

## Consumer contract

GOV-02 through GOV-10 must consume this policy contract rather than learning an expected baseline from current GitHub settings. Live settings, workflow inventories, review state, waivers, and evidence are inputs to compare against the expected values above.
