# Required-check governance contract

Issue #629 defines a three-layer, fail-closed contract for merge-blocking status contexts:

1. **Static topology** — repository workflows/jobs must continue to emit the registered context without event-level path filtering, job-level broad skips, undeclared dependency-induced skips, or `continue-on-error` masking. A required workflow job may depend only on prerequisites explicitly registered in `governance/required-checks.json`; the validator follows each prerequisite chain recursively. Conditional work is allowed only with the exact registered routing predicate and an `always()` aggregate that checks the result.
2. **Live ruleset topology** — the active default-branch ruleset must require exactly the registered contexts, with strict required-status-check semantics and the registered integration identity where GitHub supports pinning it.
3. **Exact-head evidence** — only results attached to the PR's authoritative `.head.sha`, re-fetched from the Pull Request API by trusted default-branch code, can satisfy a gate.

`governance/policy.json` / `GOV-CHECKS-001` remains authoritative for which logical checks are required. `governance/required-checks.json` is the operational registry: it adds a stable logical gate ID, producer identity, source workflow/job, trigger contract, scope, accepted conclusion, timeout/staleness policy, ruleset integration binding, explicit same-workflow prerequisites, and rename state. `scripts/ci/check-required-pr-checks.py` rejects a registry projection that differs from `GOV-CHECKS-001` and rejects any prerequisite topology not declared by the registry.

## Required-job prerequisites

The registry declares `dotnet-build` as the prerequisite of both `build-test` and `security-scan`, and `frontend-build` as the prerequisite of `frontend-test`. Both producers depend on the shared `changes` preflight/router job. The consumers use `always()` and require producer success before consuming their artifacts, so a failed or cancelled producer still results in an explicit failed required check.

`functional-fast` and `performance-fast` also use `always()`. Their expensive prerequisites may use only the exact predicates in `conditional_prerequisites`; the router itself remains unconditional. Functional NOT_APPLICABLE requires a successful router, a validated documentation-only reason and skipped runtime/suite jobs. API performance applies its independently validated route contract. Unknown or invalid diffs execute the suites. Missing, failed or cancelled routed work cannot satisfy either aggregate. The validator rejects changed predicates, undeclared conditional jobs, failure masking, cycles and missing prerequisites.

## Result semantics

A required gate passes only on `success` from the registered producer on the authoritative current head SHA. `queued`, `in_progress`, and commit-status `pending` remain pending only inside the registered timeout. Missing current-head evidence, previous-head-only evidence, timeout, `failure`, `timed_out`, `action_required`, `cancelled`, `skipped`, `neutral`, `stale`, unknown states, or producer/workflow drift never become PASS.

For GitHub Actions check runs, the evaluator requires the registered GitHub App integration and resolves the Actions run referenced by `details_url` back to the registered workflow path and `pull_request` event. The registry contains ordinary GitHub Actions check runs: `build-test`, `frontend-test`, `security-scan`, `publication-readiness`, `functional-fast`, and `performance-fast`. New contexts must be proven on a current PR head and added to the live ruleset before this migration is considered complete; a registry change alone does not establish active protection.

### PR merge checks and Main candidate obligations

The six PR contexts above are the repository's registered merge-check contract.
The active [Main protection ruleset](https://github.com/NYGsatoshi/Coglatas/rules/22302146)
was inspected on 2026-10-04 and still required only `build-test`, `frontend-test`,
`security-scan`, and `publication-readiness`, with strict semantics and GitHub
Actions integration 15368. Registration and published checks therefore do not
establish six-context live protection. Activation requires reconciliation of
that ruleset with the registered contexts and trusted evaluation of the current
PR head; live drift remains blocking under the existing governance contract.
Re-read live state before reporting activation.

The manual MVP-A final verifier has a separate exact-Main-candidate contract.
Its nine required GitHub Actions contexts are defined by
[`verify-mvp-a-final-checks.py`](../../scripts/ci/verify-mvp-a-final-checks.py):

- `Main Test / Frontend / Security / Main Test`
- `Main Test / Frontend / Security / Main Frontend`
- `Main Test / Frontend / Security / Main Security`
- `publication-readiness`
- `frontend-static-analysis`
- `Real-backend E2E from main artifacts / licensed-real-backend`
- `functional-full`
- `sbom-source`
- `SBOM image scan from main artifacts / sbom-image-trusted`

These Main obligations are not additions to the six-context PR ruleset.
Bare PR check names cannot alias the nested Main contexts. The final verifier
requires trusted, successful results for the exact candidate and separately
validates Full evidence bound to its Main push/run/attempt; it rejects missing,
failed, cancelled, pending, or stale required evidence. See
[`functional-execution-evidence.md`](./functional-ci/functional-execution-evidence.md)
for artifact provenance, the implemented owner slice, and verification limits.
The MVP-A candidate result does not substitute for #482 integrated regression
or #481 public HTTPS production release evidence.

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
