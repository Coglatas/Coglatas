# Frozen Qodana identity reconciliation

This read-only proof compares the complete successful Qodana inventories for the
frozen audit and source main 2f1b9b711901a84bc425b1a4ce82eff76477e3d7.
It changes no application source, inspection budget, suppression or frozen packet helper.

The original Cloud artifact was replaced during a rerun. The original Deep raw
SARIF remains byte-pinned to 6f1b59fe7bbcccdd97534e063e2f7205d3e722958f962664cac2352e7b81091b.
Issue #976 independently established identical original/R2 identities and result
indices. The verifier checks all 2480 ordered original/replay identities before
using any frozen packet index. Historical Cloud digest
95a0556a7cedce7b9d4d6a6a49af9af710ffd2c414289d946b84ae4f6ff0bb4c and replay digest
293b9b535ca32e87c1737297414816b4c271bf7123d5070520193814ac8209d0 remain distinct.

Twelve landed packets select 94 exact findings for removal. P07/P09/P10 remain held.
A count subtraction alone is insufficient: the complete retained identity
multiset must match the actual head inventory.

## Reviewed retained fingerprints

Qodana's equalIndicator/v1 also includes surrounding source context. The strict
initial comparison detected 14 changed fingerprints, all with the same rule,
path, message and exact source-token location. The warnings remain present.

| Packet | Retained warnings | Source review |
| --- | ---: | --- |
| P01 | 8 | DTO declarations unchanged; nearby enum qualification shortened |
| P06 | 1 | Async wrapper unchanged; audited unused private parameter removed |
| P11 | 3 | Mutable EF properties retained; equivalent constant initializer uses nameof |
| P12 | 1 | Positive-arm operator retained; audited equivalent false arm changed |
| P14 | 1 | Public Id property retained; private field reference renamed |

[qodana-retained-correspondences.json](qodana-retained-correspondences.json)
records each complete old/new identity, both actual SARIF regions/context,
both source blob hashes, declaration lines, packet and review rationale.
No inferred or global fingerprint normalization is used. Each mapping must occur
exactly once, refer to a landed packet, and bind to both immutable source/SARIF
revisions. The API response path/blob and actual Git blob bytes must match.
The retained token and declaration must match their exact source coordinates.
Duplicates, absent mappings, unexpected warning additions/removals, other
fingerprint changes and changed rule/path/message remain blocking.

A verified correspondence is retained debt, never source remediation. The proof
reports mapped_retained_findings separately from measured_removals and preserves
both original and current identities in the artifact. Inspection budgets stay
unchanged. Tests cover provenance drift, duplicate/multiplicity errors, source
blob/region mismatch, token forwarding and unexpected findings.

This isolated continuation preserves draft #999's exploratory failed results
without rewriting history or suppressing Gitleaks. Its unrelated OpenAPI
diagnostic was moved to #1001. Main's actual runtime security failure remains
separate; reconciliation success does not establish all-CI success or authorize
the pending OpenAPI patch or a budget ratchet.
