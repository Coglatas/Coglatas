# Qodana Code Scanning individual review — batch 2

Date: 2026-10-02. Repository: `NYGsatoshi/Coglatas`.
Working branch: `ops/qodana-code-scanning-triage-20261002`.

## Verified outcome

The existing triage branch was resumed. No application source, Qodana rule
configuration, inspection baseline, or main branch was modified by this batch.
The working branch has not been merged.

| Item | Count |
| --- | ---: |
| Newly dismissed as `false positive` | 94 |
| Previous inadequately justified dismissals reopened for review | 4 |
| Net reduction in open Qodana alerts | 90 |
| Qodana open before this batch | 1,302 |
| Qodana open after this batch | 1,212 |
| Qodana dismissed after this batch | 1,176 |
| SonarCloud open, unchanged | 30 |
| Unlisted alert state changes observed between snapshots | 0 |

All 98 changes were individually confirmed with a GET after the PATCH.
The workflow and its receipt both completed successfully. The after-snapshot
also confirmed every enumerated final state. The source main ref remained at
the reviewed revision through execution.

## Evidence and execution identity

- Reviewed source: `1a10f51e2635cf736e0daf2fc3b8a84628cd4bb1`.
- Alert analysis revision: `634b52d77022ba60bdd53e7389bc0435d2dad100`.
- Read-only evidence inventory run: `36992579226`.
- Apply commit: `84060844c6a475bfeb15e9011bdd871136034d1f`.
- [Successful apply run 36993598450](https://github.com/NYGsatoshi/Coglatas/actions/runs/36993598450).
- Apply job: `110795146042`.
- [Evidence artifact 11220997495](https://github.com/NYGsatoshi/Coglatas/actions/runs/36993598450/artifacts/11220997495).
- Artifact SHA-256: `a5acebf9d70614a1e4d3a6fd25fdd40d73a309d3b2a1bbed4efd6d53d9e76e8b`.
- Executed manifest SHA-256: `ebc90187cfcbe4ad8502ed02464bde3a44b2a9823dad7c9aa251b9b81229ed0f`.
- Last verified alert mutation: `2026-10-02T10:11:57Z`.

The artifact contains `review-receipt.json`, `review-manifest.json`,
`alerts-after.json`, the pre-application alert inventory, and exact-source
review files. Its configured retention is 14 days. The committed decision
manifest is `scripts/quality/qodana-reviewed-alerts.json`; each alert ID has
an exact rule, declaration location, message or symbol, rationale and evidence
references. Repeated nested DTO evidence is shared explicitly, not inferred
from a type suffix.

## Newly dismissed alerts

| Inspection | Count | Reviewed usage |
| --- | ---: | --- |
| `NotAccessedPositionalProperty.Global` | 89 | Actual HTTP JSON, nested response DTO, outbox JSON or SignalR return paths |
| `NotAccessedPositionalProperty.Local` | 1 | Project activation command response serialized through the canonical envelope |
| `ClassNeverInstantiated.Global` | 3 | Actual ASP.NET request deserialization/model binding, including a nested manifest record |
| `UnusedType.Global` | 1 | EF Core design-time `IDesignTimeDbContextFactory<AppDbContext>` discovery |

Explicit dismissed IDs:

```text
217 218 219 945 946 947 998 999 1000 1001
1010 1011 1012 1013 1014 1015 1019 1020 1021 1022 1023 1024
1395 1396 1397 1398 1399 1400 1409 1410
1436 1437 1438 1439 1440 1441 1442 1443 1444
1448 1449 1450 1451 1452 1453
1510 1511 1512 1513 1514 1515 1516 1517 1518
1519 1520 1521 1522 1523 1524 1525 1526 1527 1528 1529
1531 1532 1533 1534
1573 1574 1575 1589 1628 1629 1630 1631 1632 1656 1657 1658 1659
1671 1672 1884 1885 1886 1887 1888 1889 1890 1891 1956 3174
```

## Corrections to the previous blanket getter rule

The following `UnusedAutoPropertyAccessor.Global` alerts in
`src/Coglatas.Application/Announcements/AnnouncementDtos.cs` were reopened:

| Alert | Declaration | Line |
| --- | --- | ---: |
| 2938 | `CreateAnnouncementRequest.Attachment.get` | 135 |
| 2939 | `UpdateAnnouncementRequest.Attachment.get` | 180 |
| 2947 | `CreateAnnouncementRequest.Cta.get` | 134 |
| 2948 | `UpdateAnnouncementRequest.Cta.get` | 179 |

The old file-wide classifier did not demonstrate execution of these getters.
The constructors encode CTA/attachment inputs into Body and the create/update
service consumes Body; OpenAPI metadata alone does not establish a getter read.
Reopening means further individual review is required, not that a runtime defect
has been confirmed. No DTO contract was deleted or changed.

Previous draft-content getter alerts 2937 and 2946 remain dismissed: the actual
`AnnouncementDraftService.CreateAsync` path calls `Fingerprint(request)`, whose
`JsonSerializer.Serialize(request)` reads the nested DraftContent getters.

## Safeguards and remaining scope

The old Response-suffix and path-wide automatic dismissal rules were removed.
The replacement validates 30 source-file SHA-256 hashes and each approved alert's
ID, rule, path, line, complete diagnostic, tool `QDNETC`, main ref, category and
reviewed analysis revision. It validates the complete batch before the first
write, rechecks each alert immediately before its update, and verifies each
written state afterwards. Only enumerated IDs can be changed; other tools are
out of scope. Reopening additionally requires the previous bot identity and
this task's original false-positive dismissal comment prefix.

Only the exact apply commit subject specified in the manifest enables writes.
Other workflow-triggering commits use read-only validation, and reapplying an
already completed decision does not duplicate the mutation.

The remaining 1,212 open Qodana alerts have not all received individual review.
They must not be collectively treated as either confirmed defects or false
positives. In particular, internal unused data, unused request members and
style/immutability suggestions were not dismissed merely because they appear
in DTO/configuration/test code.

The previous batch's 1,048 Response-suffix positional-property dismissals have
not all been re-audited in this batch. That limitation remains explicit.

This was source-and-usage-path review, not a fresh Qodana scan or application
runtime verification. No .NET build or application integration tests were run.
The local applier checks covered 98 exact alert identities, 30 source hashes,
8 tampered-identity rejections, a fake-API state-transition test and idempotent
reapplication. Those local tests were separate from the real verified GitHub
changes reported above.
