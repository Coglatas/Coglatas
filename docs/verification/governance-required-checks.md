# Required-check governance contract

Issue #629 defines a three-layer, fail-closed contract for merge-blocking status contexts:

1. **Static topology** — repository workflows/jobs must continue to emit the registered context without event-level path filtering, broad job-level skips, unchecked dependency-induced skips, or `continue-on-error` masking. A required job may aggregate parallel workers only when it runs under `if: always()` and fail-closes over every declared `needs` result through `scripts/ci/require-needs-success.py`.
2. **Live ruleset topology** — the active default-branch ruleset must require exactly the registered contexts, with strict required-status-check semantics and the registered integration identity where GitHub supports pinning it.
3. **Exact-head evidence** — only results attached to the PR's authoritative `.head.sha`, re-fetched from the Pull Request API by trusted default-branch code, can satisfy a gate.

`governance/policy.json` / `GOV-CHECKS-001` remains authoritative for which logical checks are required. `governance/required-checks.json` is the operational registry: it adds a stable logical gate ID, producer identity, source workflow/job, trigger contract, scope, accepted conclusion, timeout/staleness policy, ruleset integration binding, and rename state. `scripts/ci/check-required-pr-checks.py` rejects a registry projection that differs from `GOV-CHECKS-001`.

## Aggregate required jobs

The merge-required context may be a lightweight aggregate job so expensive checks can execute in parallel without changing the stable required-check identity. Aggregate jobs are permitted only under the following fail-closed topology:

- the required job retains its registered job ID, context name, workflow, trigger, timeout, and GitHub Actions producer;
- every internal worker is listed in the required job's `needs`;
- the required job uses exactly `if: always()` so worker failure, cancellation, or skip cannot suppress the required context;
- the required job passes `${{ toJSON(needs) }}` as `REQUIRED_NEEDS_JSON` to `scripts/ci/require-needs-success.py`;
- the guard accepts only `success` for every declared dependency. Missing, malformed, failed, cancelled, or skipped dependency results are blocking.

This preserves the exact-head required context while allowing backend, frontend, and security work to fan out internally.

## Result semantics

A required gate passes only on `success` from the registered producer on the authoritative current head SHA. `queued`, `in_progress`, and commit-status `pending` remain pending only inside the registered timeout. Missing current-head evidence, previous-head-only evidence, timeout, `failure`, `timed_out`, `action_required`, `cancelled`, `skipped`, `neutral`, `stale`, unknown states, or producer/workflow drift never become PASS.

For GitHub Actions check runs, the evaluator requires the registered GitHub App integration and resolves the Actions run referenced by `details_url` back to the registered workflow path and `pull_request` event. The current merge-required registry contains only ordinary GitHub Actions check runs: `build-test`, `frontend-test`, `security-scan`, and `publication-readiness`.

The live evaluator entry point is:

```bash
python3 scripts/ci/check-required-pr-checks.py \
  --live-pr "$PR_NUMBER" \
  --repository "$GITHUB_REPOSITORY" \
  --json
```

A trusted workflow may fetch the evaluator, policy and registry from the default branch into a temporary directory and pass them explicitly:

```bash
python3 "$TRUSTED_REQUIRED_CHECK_EVALUATOR" \
  --policy "$TRUSTED_POLICY" \
  --registry "$TRUSTED_REQUIRED_CHECK_REGISTRY" \
  --live-pr "$PR_NUMBER" \
  --repository "$GITHUB_REPOSITORY" \
  --json
```

Live mode does **not** execute repository-static validation from the temporary directory. Static topology remains a PR/default-branch CI responsibility; live mode consumes only the explicitly supplied trusted policy/registry plus authoritative GitHub API state.

Exit codes are `0` for pass, `2` for pending, and `1` for fail/unknown/API error. GOV-02 must translate the JSON decision without converting API, parsing, producer, or timeout failures into success.

## No-gap rename protocol

Required context, workflow, or job renames are migrations, not one-step edits. Set `rename.state = "dual-publish"` and record the previous identifier(s) plus a tracking Issue before removing anything.

During `dual-publish`:

1. The static validator requires the current producer and any separately named previous workflow/job to remain present.
2. The live ruleset validator expands the migration contract so the old and new required contexts are both recognized as expected. A legitimate old context is not classified as unknown drift.
3. The exact-head evaluator requires current-head evidence for both distinct old and new producer identities when they are distinguishable by context/workflow.
4. A missing old or new migration signal is blocking.

Recommended order:

1. Add/publish the new signal while preserving the old signal.
2. Set the registry entry to `dual-publish` with the previous identifiers and tracking Issue.
3. Add the new context to the live ruleset. Where the context name itself changes, both old and new contexts are required during this phase.
4. Verify repeated PR current heads produce both expected signals from the expected producer and GOV-02 reports no required-check drift.
5. Remove the old context from the live ruleset only after the new signal is stable.
6. Remove the old producer/workflow/job.
7. Return the registry entry to `stable` and clear all previous identifiers and `migration_issue`.

Never rename a required job/context and update only one of workflow, registry/policy, or ruleset. Partial migration either weakens protection or creates an indefinitely pending required context and is expected to fail this contract.

## Adding future aggregate gates

When `ci/functional`, `ci/performance`, `ci/compatibility`, `ci/governance`, or another aggregate becomes merge-required, add it to `GOV-CHECKS-001` and `governance/required-checks.json` in the same reviewed change. Define its stable gate ID, producer, trigger/scope, exact timeout, and ruleset integration before making the live ruleset require it. Then use the same no-gap ordering above to activate it without a missing-context window.
