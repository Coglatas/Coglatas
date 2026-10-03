# Functional CI deterministic full-stack harness

Issue: #581 (FCI-02)

## Canonical command

Run the deterministic real-backend Functional stack with:

```bash
npm run test:functional
```

The reusable orchestration lives in `scripts/ci/functional-compose-harness.sh`.
Existing real-backend Node runners remain supported for compatibility and can
migrate incrementally rather than through a flag-day rewrite. The default Node
runner now executes migrated `tests/functional/` owners with their dedicated
Playwright config before running the legacy `tests/ui/` regression config, so
both discovery roots fail closed independently.

For the canonical FCI-04 core owner, run:

```bash
node tests/ui/run-real-backend-fci04.mjs functional-fast functional-full
```

This uses the existing Node Compose lifecycle with one isolated real stack and
executes the same `FUNC-TASK-001` owner once per explicit gate. The P0 manifest
runner requires `functional-fast` before the legacy suite; Main's protected
artifact-reuse acceptance explicitly selects `functional-full`. Required owner
execution rejects missing, skipped, retried, mistagged, or incomplete results.
Selecting only unrelated legacy tests cannot satisfy the owner. Fast/full
completion records are written to `test-results/fci04-<gate>-owner.json` and
contain no protected bodies or tokens.

## Lifecycle contract

The shell harness owns this common sequence:

```text
validate Docker/Compose host
-> validate Compose config
-> build application + Playwright images
-> create/use an isolated Compose project
-> start PostgreSQL
-> wait for PostgreSQL readiness
-> apply EF Core migration head
-> require migration exit 0
-> start production-like ASP.NET Core host + built Angular assets
-> Test-only deterministic fixture seed completes during host startup
-> wait for /health/ready
-> execute suite
-> collect sanitized diagnostics on failure
-> docker compose down --volumes --remove-orphans
```

The default Compose project name includes the CI run, attempt, and Bash process id. `FUNCTIONAL_COMPOSE_PROJECT_NAME` is an explicit override; the older `REAL_BACKEND_SMOKE_COMPOSE_PROJECT_NAME` remains accepted during migration. `FUNCTIONAL_COMPOSE_FILES` may provide a comma-separated base/overlay list for future domain lanes.

The base stack does not publish fixed application/database host ports, so network and named-volume resources remain namespaced by the Compose project.

## Canonical deterministic fixture

The common prerequisite fixture remains the Test-only application seeder `AppDbContextSeed.SeedBrowserSmokeAsync`. The harness explicitly enables that boundary and defaults to the established synthetic actor:

- actor: `e2e-user@example.test`
- secondary/restricted actor already present in the fixture: `browser-smoke-recipient@example.test`
- Workspace: `browser-smoke-workspace` / `Browser Smoke Workspace`
- Project: `browser-smoke-project` / `Browser Smoke Project`
- Task: `Browser smoke task`
- eligible Task FileObject: `browser-smoke-task.txt`
- Announcement: `Browser smoke announcement`

The harness rejects a primary actor email outside the reserved `@example.test` domain. The seed is prerequisite data, not a general-purpose direct-DB backdoor: journeys intended to validate creation must continue through the public/application path.

## Reset and retry policy

The default reset boundary is the whole Compose project:

```text
unique project -> clean DB/volumes -> run -> down --volumes --remove-orphans
```

PostgreSQL, network, upload storage, data-protection keys, and other named volumes are not shared with another project. Fixture provisioning is idempotent so retried setup does not create duplicate logical membership/resources.

## Failure classification

Setup failures use:

```text
[INFRA/SETUP FAILURE] phase=<phase>: <message>
```

A non-zero suite after readiness uses:

```text
[PRODUCT TEST FAILURE] phase=execute-suite: <message>
```

This keeps Docker/image/migration/readiness failures distinct from product Functional regressions.

## Sanitized diagnostics

On failure, bounded diagnostics are written under `test-results/`:

- Compose container status
- migration container status
- log tails for PostgreSQL, migration, app, and test services
- Playwright traces/screenshots/reports produced by the test runner

Before persistence, the harness redacts password, connection-password, authorization, cookie, CSRF, invite-token, generic token/secret/license patterns and known runtime values including `SYNCFUSION_LICENSE` and the synthetic browser password. Generic non-JSON environment/config key-value forms are part of the contract: `TOKEN=...`, `ACCESS_TOKEN: ...`, `REFRESH_TOKEN=...`, `*_SECRET=...`, `*_LICENSE: ...`, and equivalent case/hyphen variants are redacted. Unrelated values such as `SAFE_VALUE=...` are preserved so diagnostics remain useful. Raw protected response bodies and credential material must not be added to diagnostics.

## Syncfusion license trust boundary

The protected full real-backend lane remains `.github/workflows/licensed-real-backend-acceptance.yml`:

- trigger: Main CI's reusable-workflow call after its build producer; `push`
  to the existing integration branch or trusted `test/fci04-*` write-access
  branches; explicit `workflow_dispatch` (including the focused `fci04` suite)
- protected environment: `syncfusion-licensed-build`
- license source: `${{ secrets.SYNCFUSION_LICENSE }}`
- checkout: exact `${{ github.sha }}`
- credentials persistence: disabled
- no `pull_request` or `pull_request_target` execution

`scripts/ci/verify-functional-trust-boundary.sh` verifies these invariants from the PR-safe `Real Backend P0 Preflight`. An untrusted PR can therefore validate the contract without receiving the protected license.

The trusted FCI-04 branch push runs only the focused fast/full core owner and
does not start unrelated legacy/MBJ lanes. It preserves exact-SHA checkout and
the protected environment; PR-safe preflights never receive the license.

## PR-safe self-test

The preflight runs:

```bash
bash -n scripts/ci/functional-compose-harness.sh
bash scripts/ci/functional-compose-harness.sh --self-test
bash scripts/ci/verify-functional-trust-boundary.sh .github/workflows/licensed-real-backend-acceptance.yml
```

The self-test covers project-name sanitization, synthetic fixture identity enforcement, generic token/secret/license key-value redaction (including the forms above), preservation of non-secret key-values, the destructive-cleanup command contract, and both failure-classification markers without starting Docker or consuming a license.

## Migration guide for existing runners

Existing `run-real-backend-*.mjs` and MBJ scripts remain valid. When a lane migrates, move only generic infrastructure responsibilities into the shared shell harness: project isolation, base build/start, PostgreSQL/migration/app readiness, diagnostics/redaction, and cleanup. Keep domain-specific probes, overlays, restart phases, and persistence assertions in the domain runner.
