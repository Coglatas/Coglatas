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

The repository now implements the bounded artifact-only domain contract
described in [`functional-execution-evidence.md`](./functional-execution-evidence.md).
The target scopes in sections 1, 2, and 5 remain unchanged: the implemented
seven-owner Full/Extended slice does not complete every historical P0/P1 row.

| Current mechanism on `main` | Current behavior | Canonical Functional interpretation | Continuing owner |
| --- | --- | --- | --- |
| `.github/workflows/ci.yml` | `pull_request` build/unit/static/mock/visual checks plus conservatively routed real `functional-fast`; its stable aggregate always checks routing and execution results | The public Test-only runtime consumes shared build artifacts for the tested merge SHA and records the PR head separately. Relevant or unknown changes run all four Fast domains and six canonical owners. A validated documentation-only exemption requires successful routing and skipped runtime/domain jobs | FCI-08 (#605); domain journeys retain their stable owners |
| `.github/workflows/main-build-artifacts.yml` with `functional-validation.yml` | Every Main push produces the exact-SHA runtime and calls all four Full domains; the always-evaluated `functional-full` aggregate requires producer and domain success | The canonical artifact-only Full contract requires seven owners on the first attempt, including all eleven core completion steps. It is the implemented domain slice, with no P0 path-routing exemption | FCI-09 (#611); remaining historical matrix acceptance stays explicit |
| `.github/workflows/functional-extended.yml` | Nightly at 03:17 JST and explicit dispatch execute the existing Full expansions for current Main or an exact ancestor, using the latest trusted exact-SHA Main run only after it is completed and not cancelled; no older run substitutes | Implemented extended execution and provenance plumbing; it does not claim every P1 owner or expanded negative/realtime case is complete | FCI-09 (#611) |
| `.github/workflows/licensed-real-backend-acceptance.yml` | The Main hub independently calls P0, authz, My Tasks, and MBJ-01/02/03 acceptance using its artifacts; manual dispatch can select focused suites | Independent acceptance remains required by the final verifier. Its legacy owners and expansions are mapped to stable matrix IDs and are not replaced by the seven-owner domain contract | Existing suite owners and FCI-09 (#611) |
| `tests/ui/run-real-backend-p0.mjs` | Manifest-selected Compose-backed P0 acceptance | Retained independent legacy acceptance and focused expansions; canonical domain owners are identified separately in the matrix | FCI-03/04/05/06/07 and FCI-09 |
| `tests/ui/run-real-backend-my-tasks.mjs` | One manifest-verified real My Tasks acceptance | Owner for `FUNC-TASK-002`; the core domain's My Tasks discovery step does not replace this owner | Retained under FCI-03/09 |
| MBJ-01/02/03 scripts | Dedicated real backend acceptance with isolated fixtures | Real owner evidence for bootstrap/invite/session stable IDs | Retained as focused owner/extended suites unless consolidated explicitly |
| `scripts/ci/run-mvp-a-authz-boundary-acceptance.sh` | Anonymous/admin/member/CSRF/logout boundary against the real backend | Independent authorization acceptance alongside the canonical `FUNC-AUTHZ-001`/`FUNC-AUTHZ-002` domain owners | Existing authz owner and FCI-07/09 |
| `.github/workflows/public-https-golden-path.yml` | Protected manual test against a real public HTTPS deployment | Downstream `functional-release` deployment projection for #481; intentionally not a PR check | FCI-10 (#615) consumes evidence; #481 owns the public path |
| `.github/workflows/mvp-a-final-gate.yml` | Manual exact-commit aggregator on `main`; requires nine authoritative Main check contexts, including `functional-full`, and validates its exact SHA/run/attempt artifact | MVP-A candidate evidence consumer. Its Main obligations are separate from the six-context PR registry, and it does not replace #481/#482 production release requirements | FCI-10 (#615) |
| #482 | Open terminal cross-screen regression Issue | Downstream integrated regression evidence for `functional-release`; does not replace domain owner journeys | #482 + FCI-10 |

### Important current-state distinction

`ci.yml` owns PR validation, while the Main artifact hub owns Main Full
execution. The stable PR `functional-fast` gate is implemented; Functional
policy/harness documentation remains relevant and selects Fast. Other
positively identified documentation may produce the explicit
`NOT_APPLICABLE: validated documentation-only change` aggregate result.
Missing, failed, or cancelled routed execution cannot use that exemption.

The configured topology does not establish hosted Main Full execution.
Exact-SHA/run/attempt evidence remains required before runtime readiness is
recorded; the target P0/P1 scopes and independent acceptance requirements
remain unchanged.

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
