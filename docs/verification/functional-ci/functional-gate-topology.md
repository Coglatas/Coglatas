# Functional CI gate topology

Status: canonical gate ownership/topology for Issue #577 / FCI-01.

The journey inventory is in
[`functional-journey-matrix.md`](./functional-journey-matrix.md). Definitions of
real-functional coverage, result states, quarantine, duplicate ownership, and
runtime budgets are in
[`functional-test-policy.md`](./functional-test-policy.md).

The artifact-only canonical domain execution and schema-v1 evidence slice is
described in [`functional-execution-evidence.md`](./functional-execution-evidence.md).
Use its explicit source/runtime qualifications when reconciling #605/#611/#615;
the existing historical matrix owners and #481/#482 remain separate acceptance
requirements.

## 1. Target topology

```text
pull_request
  ├─ existing build/unit/static/mock/visual checks
  └─ functional-fast                         [required when relevant]
       ├─ bounded core P0 owner journey(s)
       ├─ changed-domain P0 owner journey(s)
       └─ representative auth/session/CSRF/authz denial

main push
  └─ functional-full                         [all P0]
       ├─ auth/session/onboarding
       ├─ workspace/membership
       ├─ project/task/execution
       ├─ files
       ├─ messaging
       ├─ notification/read-state
       └─ cross-scope authorization/privacy

nightly / workflow_dispatch
  └─ functional-extended
       ├─ P1 journeys
       ├─ expanded negative matrices
       ├─ retry/idempotency/reload/realtime cases
       ├─ longer cross-domain journeys
       └─ quarantine rechecks

release candidate exact SHA
  └─ functional-release
       ├─ require exact-SHA Functional evidence
       ├─ #482 integrated cross-screen regression evidence
       ├─ deploy candidate
       ├─ #481 public HTTPS production Golden Path evidence
       └─ MVP-A final/release decision consumes all required evidence
```

The four Functional gate names are stable contracts:

- `functional-fast`
- `functional-full`
- `functional-extended`
- `functional-release`

## 2. Gate ownership contract

| Gate | Trigger | Required scope | Blocking semantics | Typical environment | Primary owner |
| --- | --- | --- | --- | --- | --- |
| `functional-fast` | relevant `pull_request` | smallest deterministic P0 real-functional subset affected by the change plus representative auth/session/CSRF/authz denial | Every selected required journey must be `PASS`; `SKIPPED`, `BLOCKED`, `QUARANTINED`, or missing owner is not green | isolated Compose, migrated PostgreSQL, production Angular | FCI-08 (#605) wires selection/required check; domain journey remains owned by its stable ID |
| `functional-full` | `push` to `main` | all P0 stable IDs from the matrix | all P0 owner journeys must execute; no P0 can be silently skipped by path routing | isolated/full-stack environment with deterministic fixtures | FCI-09 (#611), using FCI-02/03 harness and FCI-04..07 journeys |
| `functional-extended` | nightly and `workflow_dispatch` | P1, expanded P0 negative/reload/realtime/idempotency cases, quarantine rechecks | normal nightly result is explicit; quarantine cannot satisfy an owner slot; explicit release policy may require selected extended lanes | isolated full-stack lanes, sharded where needed | FCI-09 (#611) |
| `functional-release` | explicit release candidate on exact SHA after prerequisite Functional checks | exact-SHA Functional evidence plus downstream integrated/deployment evidence | release is blocked when required exact-SHA evidence is missing, stale, failed, skipped, blocked, or belongs to another commit | protected release/deployment environments | FCI-10 (#615) plus #482/#481/final-gate consumers |

## 3. Relevance and PR routing

`functional-fast` is required **when relevant**, not for every documentation-only
or unrelated change. Relevance is a routing decision; it does not change the
journey's P0/P1 classification.

Rules:

1. changing a journey owner test, its fixture/harness, its required-test
   manifest, or its domain production code selects that journey;
2. changing shared auth/session/CSRF/tenancy/persistence infrastructure selects
   representative core and authorization journeys even if a single feature
   directory did not change;
3. changing only docs may legitimately produce `NOT_RUN` for Functional CI;
4. path routing must fail safe for unknown/shared files rather than silently
   excluding all Functional coverage;
5. a selected journey may not be converted to green by `test.skip`, missing
   fixture, missing secret/environment, or runner outage;
6. release-only suites are not automatically selected in pull requests.

Issue #494 provides the repository's changed-file routing precedent. FCI-08 owns
binding that mechanism to Functional journey IDs without weakening these rules.

## 4. Current repository topology and migration target

Issue #577 is a taxonomy/ownership issue. It documents the target topology
without pretending the later FCI wiring already exists.

| Current mechanism on `main` | Current behavior | Canonical Functional interpretation | Migration owner |
| --- | --- | --- | --- |
| `.github/workflows/ci.yml` | PR/main build + backend tests + Angular unit/build/Storybook + Docker `angular-smoke` | existing non-functional foundation plus focused mock/visual regression. `angular-smoke` is not `functional-fast` when core routes are intercepted | FCI-08 (#605) adds the real PR Functional gate |
| `.github/workflows/licensed-real-backend-acceptance.yml` | on `main` runs P0, authz, My Tasks, MBJ-01/02/03; manual dispatch can select suites | current closest implementation of `functional-full` and explicit extended acceptance. Its existing suites are mapped to stable IDs in the matrix | FCI-09 (#611) normalizes full/nightly topology and sharding |
| `tests/ui/run-real-backend-p0.mjs` | manifest-selected Compose-backed P0 Playwright slice | reusable real-functional owner slice; future input to `functional-fast` and `functional-full` | FCI-03/04/05/06/07 and FCI-08/09 |
| `tests/ui/run-real-backend-my-tasks.mjs` | one manifest-verified real My Tasks acceptance | owner for `FUNC-TASK-002` | retained/reorganized under FCI-03/09 |
| MBJ-01/02/03 scripts | dedicated real backend acceptance with isolated fixtures | real owner evidence for bootstrap/invite/session stable IDs | retained as focused owner/extended suites unless consolidated explicitly |
| `scripts/ci/run-mvp-a-authz-boundary-acceptance.sh` | anonymous/admin/member/CSRF/logout boundary against real backend | representative P0 authz owner slice suitable for fast/full | FCI-07/08/09 |
| `.github/workflows/public-https-golden-path.yml` | protected manual test against real public HTTPS deployment | downstream `functional-release` deployment projection for #481; intentionally not a PR check | FCI-10 (#615) consumes evidence; #481 owns public path |
| `.github/workflows/mvp-a-final-gate.yml` | manual exact-commit check aggregator on `main` | release evidence consumer; must eventually require Functional evidence on the same SHA | FCI-10 (#615) |
| #482 | open terminal cross-screen regression Issue | downstream integrated regression evidence for `functional-release`; does not replace domain owner journeys | #482 + FCI-10 |

### Important current-state distinction

`licensed-real-backend-acceptance.yml` runs its acceptance suites on `main` and
manual dispatch, while `.github/workflows/ci.yml` currently runs the static/mock
Angular Playwright smoke on pull requests. Therefore the repository does **not**
yet have the final target `functional-fast` PR gate solely because this policy
exists. That implementation is explicitly deferred to FCI-08 (#605).

## 5. Gate-to-journey ownership

The matrix is the source of truth. The default ownership shape is:

### `functional-fast`

Must contain a bounded representative subset of P0 real owner journeys. At
minimum, routing must be able to select:

- `FUNC-AUTH-001`
- `FUNC-WS-001`
- `FUNC-PROJ-001`
- `FUNC-TASK-001`
- `FUNC-TASK-002` when My Tasks is relevant
- `FUNC-EXEC-001` when execution/source/result code is relevant
- `FUNC-FILE-001` when File authorization/grant code is relevant
- `FUNC-MSG-001` when messaging is relevant
- `FUNC-NOTIF-001` when notifications/read-state are relevant
- `FUNC-AUTHZ-001` and/or the representative slice of `FUNC-AUTHZ-002`

The PR gate does not need every negative permutation; it needs enough real
coverage to fail quickly on a high-risk regression.

### `functional-full`

Runs every P0 owner stable ID, regardless of changed-file routing, on `main`.
This is the canonical answer to "does current main complete every P0 functional
journey against the real application boundary?"

A P0 `NOT_IMPLEMENTED`, `SKIPPED`, `BLOCKED`, or `QUARANTINED` row keeps
`functional-full` incomplete. P0 missing coverage is visible debt; it is not
converted to a pass by narrowing the suite.

### `functional-extended`

Owns:

- all P1 owner journeys;
- expanded cross-scope authorization/privacy matrices;
- reload/restart/session expiry/realtime disconnect-reconnect cases;
- idempotency/retry and longer negative cases;
- cross-screen integrations that are too slow for PR; and
- quarantine rechecks.

An extended test may also execute on main when its runtime/determinism is
acceptable. Doing so does not make it the duplicate owner of a P0 journey.

### `functional-release`

Owns **evidence composition**, not duplicate feature implementation. The release
gate proves that the exact candidate SHA has valid Functional CI evidence and
that downstream deployment/integration checks passed where required.

## 6. #481 responsibility boundary

Issue #481 / `Public HTTPS Production Golden Path` owns the external deployment
path:

```text
Browser -> public TLS/CDN/tunnel/proxy -> forwarded headers -> ASP.NET Core
        -> secure cookie + CSRF -> core product path -> durable result
```

It verifies deployment-specific properties that Compose cannot prove, including
public HTTPS routing, redirect/HSTS, forwarded scheme, Secure cookie behavior,
and the candidate's real externally reachable path.

It intentionally does **not** own:

- every feature's PR regression;
- every domain's functional negative matrix;
- File upload/browse UX merely because it consumes an already prepared Project
  File;
- Messaging/Announcement/Audit journeys; or
- the `functional-fast` check.

#481 is `functional-release` projection evidence. Missing/unreachable target
environment is `BLOCKED`, not PASS.

## 7. #482 responsibility boundary

Issue #482 owns integrated terminal cross-screen regression after the relevant
frontend program lands. Its concern is interaction between screens and shared
UI/runtime state: Shell, responsive layout, theme, filters, drawers, focus,
loading/error state, authorization state, realtime state, and route navigation.

It intentionally does **not** replace missing domain owners. For example, a
#482 Files screen pass cannot satisfy `FUNC-FILE-002` if the Files journey only
ran against mocked routes. Likewise, a screenshot-only cross-screen check cannot
prove real persistence or authorization.

#482 contributes `functional-release` evidence and may reuse `functional-full`
or `functional-extended` domain fixtures instead of reimplementing them.

## 8. Exact-SHA release evidence

`functional-release` must bind evidence to the release candidate commit SHA.
The release decision must reject:

- a green Functional run for a different SHA;
- a rerun that changed source or fixture code without updating the candidate;
- missing required journey results;
- skipped/blocked/quarantined owner journeys represented as success;
- a #481 result from a different deployment/candidate; or
- a #482 result that does not identify the candidate under test.

FCI-10 (#615) owns the machine-readable evidence contract and integration with
`.github/workflows/mvp-a-final-gate.yml`. This document fixes the ownership rule
that implementation must obey.

## 9. Status aggregation

For a gate with selected required journeys:

```text
PASS  = every selected required owner journey is PASS
FAIL  = any selected required owner journey is FAIL
BLOCK = otherwise, if any selected required owner journey is
        BLOCKED / SKIPPED / QUARANTINED / NOT_IMPLEMENTED / missing evidence
```

`NOT_RUN` is excluded from aggregation only when routing legitimately did not
select that journey for the event. `functional-full` on `main` cannot use
changed-file routing to mark a P0 journey `NOT_RUN`.

When retries are enabled, the aggregate result may become PASS only according to
an explicit retry policy, but the initial failure remains present in evidence.
A retry does not rewrite the historical first attempt to PASS.

## 10. Required check and naming policy

- Keep the four gate class names stable.
- Jobs may have implementation-specific shard names, but one stable aggregate
  check must represent each required gate.
- Required branch protection should target the stable aggregate rather than
  transient shard names.
- Renaming an existing required Playwright test title that is listed in a
  manifest requires updating its manifest and the journey matrix in the same PR.
- Adding a new Functional owner test requires a stable journey ID and matrix
  update in the same PR.

## 11. Failure artifacts and privacy

Fast/full/extended runners may collect sanitized traces, logs, screenshots, or
JUnit evidence when useful, but artifact policy must prevent cookie/token/
password/protected-body disclosure. Public deployment evidence is stricter:
#481 intentionally disables Playwright traces, screenshots, video, HTML, and
JUnit output because the test runs against an externally reachable protected
fixture.

An artifact collection failure must not hide the original Functional failure.
Artifact upload should be best-effort after the gate has already captured the
owner result.

## 12. Implementation handoff

The ownership fixed by this document is consumed by later Functional CI work:

- FCI-02 (#581): deterministic full-stack fixture/reset foundation;
- FCI-03 (#585): Playwright suite architecture/tags/shared helpers;
- FCI-04 (#588): core Auth -> Workspace -> Project -> Task owner journey;
- FCI-05 (#591): Files owner journey;
- FCI-06 (#596): Messaging/Notification/Announcement journeys;
- FCI-07 (#601): cross-scope authorization/session/CSRF negative matrix;
- FCI-08 (#605): `functional-fast` + changed-file routing;
- FCI-09 (#611): `functional-full` + `functional-extended` execution/sharding;
- FCI-10 (#615): exact-SHA `functional-release` evidence integration.

These issues may change implementation details, but changing the taxonomy,
stable journey ownership, or gate semantics requires an explicit update to the
three canonical FCI-01 documents.
