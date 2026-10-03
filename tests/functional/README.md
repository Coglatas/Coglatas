# Functional Playwright architecture

This directory is the canonical home for **real user-journey Functional Playwright coverage** introduced by FCI-03 (#585). Existing `tests/ui/` coverage is intentionally not bulk-renamed; migration is incremental and an old test remains until an equivalent replacement is green.

## Directory ownership

New Functional specs use domain directories:

```text
tests/functional/
  fixtures/
  helpers/
  auth/
  workspace/
  project-task/
  files/
  messaging/
  notification/
  announcement/
  security-negative/
```

Directories are created when their first migrated/owned spec lands. Screenshot-only and static/mock UI regression remain outside this tree, normally in `tests/ui/`.

## Required metadata

Every Functional spec must call `functionalMetadata(...)` as Playwright test details. Metadata is not encoded only in the human title.

```ts
import { test } from '@playwright/test';
import { functionalMetadata } from '../fixtures/functional-metadata.mjs';

test(
  'persists a task through reload',
  functionalMetadata({
    journeyId: 'FUNC-TASK-001',
    gates: ['functional-fast', 'functional-full'],
    domains: ['task'],
    priority: 'p0',
    backend: 'real',
    polarity: 'positive'
  }),
  async ({ page }) => {
    // Journey body.
  }
);
```

The metadata helper emits repository-owned Playwright tags:

- base: `@functional`
- gates: `@functional-fast`, `@functional-full`, `@functional-extended`, `@functional-release`
- priority: `@p0`, `@p1`
- domains: `@auth`, `@workspace`, `@task`, `@files`, `@messaging`, `@notification`, `@announcement`, `@audit`, `@security-negative`
- classification: `@real-backend` or `@mock-backend`; `@positive` or `@negative`
- authorization negative: `@negative-authz`
- release evidence: `@release-evidence`
- traceability: `@journey-FUNC-...`

Journey IDs come from the FCI-01 matrix. The validator intentionally checks the stable ID format rather than hard-coding a registry so FCI-01 and FCI-03 can merge independently.

## Stable selection

Use `scripts/ci/run-functional-playwright.mjs`; it builds exact tag-token grep from metadata and defaults to `@real-backend` so mocked core APIs are not counted as a required real Functional gate.

```bash
node scripts/ci/run-functional-playwright.mjs --gate functional-fast --priority p0
node scripts/ci/run-functional-playwright.mjs --gate functional-full --domain files
node scripts/ci/run-functional-playwright.mjs --journey FUNC-TASK-001 -- --list
node scripts/ci/run-functional-playwright.mjs --domain security-negative --negative-authz
```

`build-functional-grep.mjs` ANDs dimensions and ORs repeated/comma-separated values within one dimension. It matches exact tag tokens, not accidental title substrings.

The runner also exports its explicit gate selection to the owner process as
`COGLATAS_FUNCTIONAL_SELECTED_GATES`. This lets one canonical owner test keep a
bounded `functional-fast` path and add its `functional-full`/extended steps
without duplicating the stable journey ID. A direct journey invocation with no
gate selection exercises the complete owner path.

The Functional list reporter prints `test.step` entries. Owner steps should
include both the stable journey ID and a stable step ID so console failures
identify the broken segment without requiring a trace artifact.

FCI-04's required execution entry point is
`node tests/ui/run-real-backend-fci04.mjs [functional-fast] [functional-full]`.
With no arguments it runs both gates sequentially in one isolated Compose
stack. The protected P0 runner also executes this same owner before the legacy
manifest (fast by default, full for Main artifact-reuse acceptance). It never
uses the legacy title grep to select the migrated owner.

The required FCI-04 reporter rejects zero/multiple owners, missing real-backend
metadata, skip/failure/retry, and missing stable owner steps. Fast requires
STEP-01/02/05..11; full requires STEP-01..11. Completion artifacts contain only
Journey ID, gate, candidate SHA, status, and step names, with no response bodies
or session material. Discovery with `--list` is static evidence only.

## Backend classification

`backend: 'real'` means the journey reaches the real application HTTP surface and authoritative persisted state for the behavior under test. A test that intercepts or fabricates success for core auth/business API routes must use `backend: 'mock'` and cannot satisfy required real Functional coverage.

Focused mocks remain useful for UI behavior, but they are not substitutes for FCI real-stack journeys.

## Shared helpers

The shared layer is deliberately narrow:

- `helpers/auth.ts` — login/logout/current-session helpers
- `helpers/csrf.ts` — `/api/security/csrf-token` and CSRF-aware unsafe requests
- `helpers/navigation.ts` — canonical Workspace/Project/Task routes
- `fixtures/aliases.ts` — deterministic fixture alias resolution; no private DB backdoor
- `helpers/authoritative-state.ts` — bounded polling of authoritative state instead of arbitrary fixed sleeps
- `helpers/safe-response.ts` — bounded/redacted failure previews
- `helpers/redaction.mjs` — recursive secret/token/password/cookie/license redaction for diagnostic material

## Selector policy

Use selectors in this order:

1. accessible role/name/label
2. stable user-visible text when appropriate
3. intentional `data-testid` when the control is otherwise unstable
4. CSS/layout/internal component structure only as a last resort

## Prohibited patterns

Do not:

- mutate private DB state to bypass authorization or accelerate the journey;
- intercept a core API and fabricate a success response in a `backend: 'real'` test;
- use arbitrary `page.waitForTimeout(...)` as the normal synchronization strategy;
- couple Functional selectors to CSS layout or internal component structure when an accessible locator exists;
- add new real Functional coverage to `tests/ui/angular-smoke.spec.ts`.

## Migration policy

- No Big Bang rename of `tests/ui/`.
- Keep legacy coverage until an equivalent tagged replacement is green.
- After parity, choose one owner test and reduce duplicates to focused regression where useful.
- Screenshot regression stays focused and separate from real Functional journeys.
- FCI-02 owns deterministic full-stack provisioning/reset; helpers here consume that public/application surface rather than reimplementing it.
- FCI-08/09 own required PR/main/nightly workflow wiring and sharding.

## Self-tests

Run the architecture contracts without a browser:

```bash
node --test tests/functional/*.node-test.mjs
node --check scripts/ci/functional-tags.mjs
node --check scripts/ci/build-functional-grep.mjs
node --check scripts/ci/run-functional-playwright.mjs
```
