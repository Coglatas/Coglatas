# Qodana packet verification

Issue #976 requires unchanged-source focused tests followed by candidate focused
and full backend/architecture execution using SDK 10.0.401 and real PostgreSQL.
The supplemental workflow runs on `qodana/packet-` PR branches with read-only
permissions, no protected secrets, ephemeral hosted runners and PostgreSQL 18.
Existing required checks, routing, budgets and test expectations are unchanged.

`selections/P02.json` or `selections/P04.json` selects an immutable baseline,
one approved packet and a stage for the matching PR branch. Separate packet
files allow source PRs to run in parallel without editing a shared selection.
The legacy `selection.json` is used only when no packet-specific file exists;
its packet must still match the branch. Unsupported branches fail closed.
The initial reviewed scope is P01/P02/P04, which have no supplied new tests.
Other packets require a reviewed runner extension before they can execute.

The workflow records the immutable PR event head and base revisions before any
checks, so a preflight failure still preserves revision evidence. If the event
base is already an ancestor of the exact checked-out head and descends from the
configured source baseline, P01/P02/P04 use that integrated base for fresh full
baseline/candidate execution. Otherwise the configured baseline remains in use.
The whole baseline-to-head diff still rejects every out-of-packet source,
dependency or test change, and all frozen preimage/canonical-result checks remain.
No moving branch is checked out and no prior-head evidence is reused.

For the R2 `baseline` stage, an integrated event base is compared against the
entire head. Only auxiliary CI files and that packet's byte-exact fixed test may
differ. The head is then the immutable test baseline, with production identical
to the event base. Without an integrated base, the original configured baseline
and strict auxiliary-only diff rule remain. Both modes retain the original
audited tests-first ancestor, pinned helper/plan and known-upstream byte checks.
Proofs record configured, event, effective baseline and comparison revisions.

P11/P13 retain a fixed-tests-only `baseline` stage. Commit each exact supplied
test on its isolated audited branch first, then normally integrate main before
selecting an immutable baseline. For this stage, the R2 helper runs only in dry-run mode on the
audited tests-first revision. Source overlap is accepted only for byte-exact
canonical upstream P06/P04/P07 edits in the explicitly mapped files. Unknown
source drift stops verification. This stage executes focused/full backend and
architecture tests without applying any production transformation; its evidence
does not establish candidate acceptance or complete either packet.

P14 also retains the unchanged-production `baseline` stage. It has no
supplied fixed test; `audit_test_sha` therefore selects the original audited
source revision. No test/source diff is allowed against an integrated event
base. The only mapped upstream source variant is canonical P04 in
AnnouncementEngagementStore. For this stage, the original R2 helper runs only in dry-run
mode and no P14 production transformation is applied.

- `prepare`: require unchanged production source; execute the baseline first,
  then run the unmodified canonical helper in a clean Linux worktree and test
  its result. This produces evidence for an uncommitted candidate.
- `verify`: repeat the baseline, reconstruct the canonical result, require the
  actual immutable PR head to match it byte-for-byte, and test that PR head.

Historical payload data is embedded with pinned digests. The helper and derived
plan are extracted outside both worktrees before execution. The owner's
2026-10-01 amendment permits only removing the explicit `<Guid>` from six P02
and thirteen P04 static `Enumerable.Contains` calls; the historical plan is
retained unchanged. No source hash, replacement count or test oracle is amended.

Each phase preserves focused discovery/TRX, full ordinary backend discovery/TRX,
architecture discovery/TRX and executed PostgreSQL test identities. The verifier
rejects zero tests, incomplete execution, skips, non-passing results, malformed
counters and changed test identities/multiplicities. It checks the live database
server version, applies migrations and rejects pending model changes. The two
full test projects together are the current backend solution test suite.

The artifact includes the canonical patch, source SHA-256 values, exact revisions,
test inventories and evidence hashes. A prepare pass does not establish an
integrated-head or main result, Qodana identity reconciliation, a budget ratchet,
or authorization for behavior-sensitive packets.

P11/P13/P14 also support `prepare` and `verify` after their unchanged-source
baseline. The original R2 helper first validates its clean audited worktree;
only after the current integrated baseline passes does the same unmodified
helper apply there. Canonical output is integrated with the independently
verified, byte-exact known upstream source using ordinary three-way file
merging. Any conflict stops the packet. Expected preimages, hashes, fixed-test
bytes, operations and counts are never amended.

For `prepare`, the committed head remains production-identical to its selected
baseline; only a separate candidate worktree receives the canonical integration.
For `verify`, the entire baseline-to-head diff permits only the packet targets,
its fixed test and auxiliary packet CI files, and every committed target must
match the exact integrated canonical result. Both stages require focused, full
backend, architecture and live PostgreSQL baseline/candidate execution with
identical test inventories, zero failures/skips and nonzero totals. Proofs
preserve the audited canonical patch, integrated candidate hashes and both
inventories. A prepare pass is evidence for an uncommitted candidate, not packet
completion or a substitute for actual-head verification and normal checks.
