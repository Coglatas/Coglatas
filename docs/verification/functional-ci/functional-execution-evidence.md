# Functional domain execution and exact-candidate evidence

Issues #605, #611, and #615. This is a coherent topology/evidence slice;
executed hosted results determine acceptance. Configured workflows, discovery,
and local contract tests are not evidence of real PostgreSQL/storage execution.

## Artifact-only execution

`functional-validation.yml` consumes one prebuilt runtime image and the matching
Release `bin/obj` graph. The restored source stamp and .NET build stamp must both
match the exact tested SHA. It uses no protected environment or credential.
`artifact-reuse-no-build` is only a nonsecret Compose-rendering sentinel; the
consumer never builds an application image.

Main uses the authoritative licensed `main-build-artifacts` producer. PR uses
its separately named public Test-only runtime artifact. A PR Test runtime is
not licensed distribution evidence. Fork PRs receive no protected build secret.

Each domain gets a separate runner, Compose project, PostgreSQL volume, storage
volume, and Data Protection state. Existing migrations, synthetic fixtures,
production Angular, backend routes, and canonical owner tests are reused.
Core business APIs remain real. Teardown discards each run's volumes on success,
failure, and handled cancellation. Runner termination can prevent artifact
upload; absence then fails the aggregate rather than becoming success.

## Canonical domain owner selection

| Domain | Fast owners | Full/extended expansion |
| --- | --- | --- |
| core | `FUNC-TASK-001` | Workspace switching, Project/Task/My Tasks discovery, Task update and persistence |
| files | `FUNC-FILE-002` | Supported search, move, sharing and version/current-pointer lifecycle |
| collaboration | `FUNC-MSG-001`, `FUNC-NOTIF-001` | Existing collaboration reload/realtime expansion and `FUNC-ANN-001` |
| authz-negative | `FUNC-AUTHZ-001`, `FUNC-AUTHZ-002` | Existing cross-scope/CSRF/current-authority negative expansion |

Full and extended must select all four domains. Fast domain relevance belongs
to the PR routing caller. The canonical core owner covers Auth/Workspace/
Project/Task/execution/result boundaries, but does not replace the separate
MBJ bootstrap/invite/session, Workspace/Project create, My Tasks, and grant
reauthorization owners in the journey matrix. Existing licensed acceptance
remains independently required by the final verifier. This slice must not be
described as exhaustive completion of every historical matrix row.

No retry is enabled. An initially failed attempt followed by success is `FLAKY`
and blocks the aggregate. Expected failures, skips, zero or duplicate owners,
interruption, quarantine, missing evidence, stale SHA, another run, and another
run attempt cannot become `PASS`. Fast/full/extended lane timeouts are 20/30/120
minutes; budget expiry remains failure.

`tests/functional/quarantine.json` starts empty. Each record needs a linked
Issue, reason, owner/domain, quarantine and review dates, Journey ID, and P0
readiness impact. Missing metadata and expired review dates fail validation.
A registry entry never authorizes skipping an owner. Nightly re-executes the
same required real owners, including full expansions, so a quarantined or
skipped required owner remains visible and blocks its gate.

## Scheduling and cancellation

Main calls `functional-full` using the exact Main CI producer. The explicit/
nightly wrapper calls `functional-extended` at 03:17 JST with current main or
an exact ancestor of main. It retrieves the latest non-cancelled Main CI
producer for that SHA and verifies artifact stamps. Missing artifacts block
execution. Extended runs use a candidate-specific concurrency group and do not
cancel an explicit candidate run. Failed/cancelled matrix lanes fail the stable
aggregate even when another lane succeeded.

## Metadata and final decisions

Schema version 1 allowlists SHA, gate, run/attempt, domain, timestamps, setup/test
durations, stable Journey IDs, classified states, attempts, and owner duration.
It contains no assertion text, headers, cookies, tokens, password/license,
connection strings, filenames, storage paths, protected bodies, or attachments.
High-risk traces/screenshots/video remain disabled. Sanitized local harness
diagnostics remain available to the runner; this slice uploads only bounded
metadata, because string redaction alone does not prove protected-body privacy.

Lane metadata is retained 14 days and validated aggregate metadata 90 days.
The stable aggregate records owners, setup/test durations, slowest owners, and
GO/NO-GO. Setup failure leaves explicit `BLOCKED` owner metadata when the runner
can still execute cleanup steps. No evidence after a hard cancellation means
NO-GO.

The final verifier requires `functional-full` alongside existing required
checks. It selects the latest trusted GitHub Actions check for the exact SHA,
binds its artifact to the trusted main push workflow/run/attempt, validates the
bounded archive and schema, and requires every canonical owner to pass exactly
once. Download redirects never forward the GitHub API token.

The MVP-A candidate decision is not the production release decision. #482
terminal integrated regression and #481 public HTTPS evidence remain separate
requirements. Their exact candidate/deployed-artifact references must be
recorded before a release GO; this slice neither fabricates those references
nor substitutes Compose or Functional results for either downstream gate.

## Verification limits and next dependency

Local schema/reporter/selection tests establish fail-closed plumbing only.
Hosted exact-commit execution, observed runtime budgets, exhaustive matrix
reconciliation, and downstream release evidence remain acceptance work.
Product DB-query failures remain real blockers; this slice changes no query.

After CI readiness, the next Avalonia work starts with #766's exhaustive P0
endpoint/DTO and Angular business/authorization reconciliation, plus #767's
platform feasibility proof, before expanding #769's dependency boundaries.
#769 already contains the Pure C# NYG.UI.Core contribution from #847; preserve
it. No Avalonia shell, renderer, platform project, or package migration is
implemented by this CI work.
