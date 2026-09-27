# Coglatas.UI Interaction State Transition Engine

Status: implementation/architecture contract for AV-MIG-42 / #814  
Scope: renderer-independent Pure C# interaction semantics

## 1. Purpose

The implementation-relevant core is not "multiple views" or "adaptive UI".
Those are embodiments.

The normative mechanism is a deterministic state transition engine:

```text
T(State, Event, CurrentAuthority)
    -> NextState
     + ProjectionEnvelope
     + ActionDecision
     + ExecutionPermit?
     + RefetchRequirement
```

A Table, Board, Timeline, Calendar, Graph, compact/mobile view, or fallback view
does not own the authoritative interaction semantics. Each renderer consumes a
projection of the same renderer-independent interaction state.

## 2. Canonical interaction state

```text
State S =
    Scope
    QueryDescriptor
    Selection
    InspectorTarget
    TemporalContext
    ConflictState
    ActiveRenderer
    CanonicalDependencies
    ContextGeneration
    AuthorityGeneration
    CanonicalGeneration
    CanonicalRevision
    LastKnownAuthority
    HasLocalDraft
```

Renderer-local mechanics such as scroll position, column width, Timeline zoom,
and Graph coordinates are deliberately excluded.

### 2.1 Three generation domains

- `ContextGeneration (Gc)`: incremented when binding/scope or temporal context changes.
- `AuthorityGeneration (Ga)`: supplied by the authority boundary and accepted only when strictly newer.
- `CanonicalGeneration (Gd)`: projection invalidation generation. It advances only when a newer canonical event affects semantic inputs of this projection.

Every projection carries:

```text
ProjectionStamp = (Gc, Ga, Gd)
```

### 2.2 CanonicalRevision and CanonicalGeneration are intentionally different

`CanonicalRevision (Cr)` expresses ordering of the authoritative event stream.
`CanonicalGeneration (Gd)` expresses whether this projection must be invalidated.

Each state also declares a Core-owned canonical dependency domain set, for example:

```text
CanonicalDependencies =
    QueryMembership
  | SelectedEntity
```

Each newer canonical event carries:

```text
RemoteUpdated(
    CanonicalRevision,
    ChangedDomains)
```

The engine first verifies:

```text
incoming Cr > current Cr
```

Then:

```text
if ChangedDomains intersects CanonicalDependencies:
    Cr = incoming Cr
    Gd = Gd + 1
    invalidate/refetch/conflict as appropriate
else:
    Cr = incoming Cr
    Gd unchanged
```

Therefore the authoritative stream can advance without automatically invalidating
a projection. This is the concrete behavioral distinction between Cr and Gd.

## 3. Action semantics and generation dependency derivation

The renderer does not send a generation mask. It sends only a Core-recognized
`ActionContractId`.

Examples:

```text
read.projection
read.canonical
mutate.canonical
restore.snapshot
branch.snapshot
resolve.conflict
```

The Core resolves that identifier to `ActionSemantics`:

- action kind;
- whether the action is context-bound;
- whether current authority is required;
- whether current canonical projection state is required.

The generation mask is then derived:

```text
D(a) = Resolve(ActionSemantics(a))

ContextBound                 -> include Gc
RequiresCurrentAuthority     -> include Ga
CanonicalConsistency=Current -> include Gd
```

Example:

```text
read.projection -> {Gc, Ga}
read.canonical  -> {Gc, Ga, Gd}
mutate.canonical -> {Gc, Ga, Gd}
```

This avoids making `Read/Mutate` itself a hard-coded generation table and prevents
a renderer from weakening freshness requirements.

## 4. Transition invariants

### INV-01 — Renderer switch is semantics-preserving

Renderer changes preserve shared semantic state and the generation stamp.

### INV-02 — Scope and temporal changes invalidate context-bound actions

A scope or temporal change increments `Gc`. Scope change also clears
scope-bound query/selection/inspector state and returns temporal state to `Now`.

### INV-03 — Non-current snapshots are read-only for ordinary mutation

```text
TemporalMode in {ExplicitVersion, Historical}
AND ActionKind = Mutate
=> deny
```

Explicit Restore and Branch remain separately evaluable.

### INV-04 — Canonical event ordering is monotonic

An equal or older Cr is ignored. Duplicate and reordered canonical events cannot
advance Cr or Gd.

### INV-05 — Projection impact controls Gd

A newer event outside this projection's `CanonicalDependencies` advances Cr but
does not advance Gd.

A newer event intersecting `CanonicalDependencies` advances both Cr and Gd.

### INV-06 — Conflict is created only for relevant canonical change

If a local draft exists and a newer relevant canonical event arrives:

```text
ConflictState = Conflict(
    BaseRevision = previous Cr,
    RemoteRevision = incoming Cr)
```

An unrelated canonical event does not create a synthetic conflict.

### INV-07 — Synchronization acknowledgement follows the relevant revision

`CanonicalSynchronized(r)` may clear `RemoteChanged` when `r` covers the last
revision that affected the projection, even if later unrelated events advanced Cr.

### INV-08 — Authority generation is immutable and monotonic

Same-generation and older authority events are no-ops. Only a strictly newer
authority generation can alter authority state. Reauthorization after revoke also
requires a strictly newer generation.

### INV-09 — UI projection never becomes authority

For an authority-protected action the engine verifies:

1. all generations in D(a);
2. authority snapshot scope;
3. authority generation;
4. current authority status;
5. Core-tracked authority state;
6. temporal gate;
7. conflict gate.

A visible/enabled control is never sufficient authorization evidence.

## 5. ExecutionPermit and commit-time race protection

Passing the interaction gate is not treated as permission to write indefinitely.

For an allowed non-read action the Core emits an `ExecutionPermit` containing:

```text
ExecutionPrecondition =
    ActionContract
    Scope
    ExpectedAuthorityGeneration
    ObservedCanonicalRevision
    RequiredCanonicalDomains
```

The authoritative server-side commit boundary must re-evaluate this precondition
inside the same transaction or critical section that performs the mutation.

The reference evaluator rejects commit when:

- scope changed;
- authority generation changed;
- authority is no longer Allowed;
- canonical history is inconsistent;
- any canonical change after `ObservedCanonicalRevision` intersects
  `RequiredCanonicalDomains`.

It may still allow commit when Cr advanced only because of changes outside the
domains relevant to the action.

Example:

```text
permit:
  observed Cr = 700
  required domains = {SelectedEntity}

701 WorkflowMetadata changed
702 Auxiliary changed
=> commit may proceed

703 SelectedEntity changed
=> reject StaleCanonical
```

This closes the interaction-decision/commit TOCTOU gap without reverting to a
single global revision equality check.

## 6. Reference action algorithm

```text
Evaluate(actionContract, sourceProjection, currentAuthority):

  semantics = CoreActionContracts.Resolve(actionContract)
  deps = ResolveGenerationDependencies(semantics)

  if required generations in sourceProjection != current state
      reject StaleProjection

  if semantics requires authority:
      if authority.scope != state.scope
          reject ScopeMismatch
      if authority.generation != state.Ga
          reject StaleAuthority
      if authority.status != Allowed
          reject AuthorityDenied

  if protected non-read and Core authority != Allowed
      reject AuthorityDenied

  if temporal snapshot is non-current and action is ordinary Mutate
      reject TemporalMutationDenied

  if unresolved conflict and action is ordinary Mutate
      reject ConflictResolutionRequired

  allow
  if non-read:
      emit ExecutionPermit
```

## 7. Concrete examples

### 7.1 Cr changes but Gd does not

```text
Projection dependencies = {SelectedEntity}
State[Cr=700, Gd=71]

RemoteUpdated(701, WorkflowMetadata)

State[Cr=701, Gd=71]
```

The authoritative stream advanced, but the current projection did not become stale.

### 7.2 Relevant canonical change

```text
State[Cr=701, Gd=71]

RemoteUpdated(702, SelectedEntity)

State[Cr=702, Gd=72]
```

An old canonical-sensitive action becomes stale.

### 7.3 Different reads can have different freshness requirements

```text
read.projection -> D(a)={Gc,Ga}
read.canonical  -> D(a)={Gc,Ga,Gd}
```

After a relevant canonical update, the first remains separately evaluable while
the second is rejected as stale.

### 7.4 Commit-time authority race

```text
interaction decision at Ga=8 -> Allowed
before commit: authority changes to Ga=9 / Revoked
atomic commit check -> reject
```

## 8. PostgreSQL Canonical Change Journal

The Pure C# Core remains persistence-independent, but a PostgreSQL persistence
adapter now implements the commit-time contract.

Current implementation:

- `src/Coglatas.UI.Core/Interaction/InteractionModel.cs`
- `src/Coglatas.UI.Core/Interaction/InteractionStateTransitionEngine.cs`
- `src/Coglatas.Infrastructure/Persistence/CoglatasUiCanonicalChangeJournalCoordinator.cs`
- `src/Coglatas.Infrastructure/Persistence/Migrations/20260924133000_AddCoglatasUiCanonicalChangeJournal.cs`
- `tests/Coglatas.Tests/CoglatasUiCore/InteractionStateTransitionEngineTests.cs`
- `tests/Coglatas.Tests/PostgreSql/CoglatasUiCanonicalChangeJournalPostgreSqlTests.cs`

The adapter stores one locked revision-head row per interaction scope and an
ordered append-only journal:

```text
canonical revision head:
    ScopeKey
    Scope identity
    Revision

canonical change journal:
    ScopeKey
    Revision
    ChangedDomains
```

For a permitted mutation, the coordinator performs the following inside one
database transaction:

```text
1. create/find and lock the scope revision head
2. read every journal record in (ObservedCr, CurrentCr]
3. load current authority through a transaction-bound callback
4. evaluate ExecutionPrecondition
5. if denied: rollback without staging the mutation
6. if allowed:
     stage the domain mutation
     advance the revision head
     append (next Cr, ChangedDomains)
     save the domain mutation
     commit all of the above atomically
```

The evaluator requires a contiguous journal range. Missing, duplicated, or
out-of-order revision history fails closed as `InvalidCanonicalHistory`.
Consequently, the database adapter cannot silently treat an incomplete history
as evidence that a race was irrelevant.

The revision-head lock serializes writers for the same scope while independent
scopes remain independent. The mutation, revision advance, and journal record
are committed or rolled back together.

The database schema also enforces the journal invariants independently of the
coordinator code:

- a revision head must be created at revision zero;
- the scope identity stored beside a head is immutable;
- a revision head may advance only by exactly one;
- an inserted journal row must use the current head revision for that scope;
- journal rows are append-only and cannot be updated or deleted.

These guards prevent an alternate write path from silently creating a gap,
rewriting ChangedDomains, or deleting evidence that the commit-time evaluator
depends on.

Authority remains use-case-specific. The callback used by
`ExecutePermittedMutationAsync` executes inside the journal transaction and
must lock, or otherwise transactionally stabilize, the authoritative permission
rows whose generation/status it returns. This boundary is explicit rather than
being inferred from cached UI authority.

Journal retention is currently fail-safe: no pruning is assumed by the
precondition evaluator. Any future compaction must preserve a verifiable lower
history bound and reject permits older than that bound unless equivalent
checkpoint evidence is available.

## 9. Required verification

The automated verification must cover at minimum:

1. renderer switch preserves semantic state and stamp;
2. scope switch invalidates context-bound actions;
3. ExplicitVersion/Historical reject ordinary mutation;
4. dependency masks are derived from Core-owned action semantics;
5. unknown renderer-supplied action contracts are rejected;
6. irrelevant canonical change advances Cr without advancing Gd;
7. relevant canonical change advances Cr and Gd;
8. projection read and canonical read can have different freshness outcomes;
9. local draft creates conflict only for relevant canonical change;
10. duplicate/reordered Cr cannot roll state backward;
11. synchronization acknowledgement can cover a relevant revision despite later unrelated Cr;
12. conflict resolution requires a newer Cr;
13. same/older authority generation cannot rewrite authority state;
14. allowed mutation emits an ExecutionPermit;
15. Core commit evaluation permits later changes outside required domains;
16. Core commit evaluation rejects later changes inside required domains;
17. Core commit evaluation rejects an authority change between interaction decision and write;
18. PostgreSQL journal commits revision head, change record, and staged mutation atomically;
19. PostgreSQL rollback leaves neither a revision head nor journal evidence for a failed first mutation;
20. concurrent writers for one scope receive distinct contiguous revisions;
21. a permitted PostgreSQL mutation survives an irrelevant canonical race but a later relevant race is rejected;
22. an authority-generation race or current revocation rejects before the staged mutation runs and does not advance Cr;
23. revoked state survives canonical/conflict transitions;
24. scope change requires reauthorization for protected actions;
25. PostgreSQL rejects nonzero initial revision heads;
26. PostgreSQL rejects revision-head identity rewrites and revision jumps;
27. PostgreSQL rejects journal rows whose revision does not match the head;
28. PostgreSQL rejects journal UPDATE/DELETE so canonical evidence is append-only.

## 10. Technical boundary

The following are not treated as the technical core:

- multiple renderers;
- shared object state;
- visualization switching;
- adaptive layout;
- role-sensitive display by itself;
- a single version/ETag comparison;
- version vectors or optimistic concurrency by themselves;
- selective cache invalidation by itself.

The technical focus is the combined mechanism in which:

1. renderer-independent interaction state is projected with Gc/Ga/Gd;
2. action freshness domains are derived from Core-owned action semantics;
3. Cr can advance without Gd when a canonical change is irrelevant to the projection;
4. relevant canonical changes drive Gd and conflict/refetch behavior;
5. current authority, temporal, and conflict conditions are evaluated in the same transition rule; and
6. an allowed non-read action carries commit-time preconditions so the server can
   atomically reject a later relevant canonical or authority race.
