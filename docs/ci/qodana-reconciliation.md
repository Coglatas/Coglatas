# Qodana R2 identity reconciliation

Issue #976 requires successful exact-main full analysis and measured identity
comparison before any separate budget ratchet. This read-only workflow retrieves
only pinned successful main Qodana Cloud runs and their complete inventory ZIPs.
Repository tokens are never forwarded to artifact storage redirects.

The original R2 manifest assigns indices only to raw SARIF SHA-256
`95a0556a7cedce7b9d4d6a6a49af9af710ffd2c414289d946b84ae4f6ff0bb4c`
from audited source `9ff983078f2ddf85f21e4e16e0c9b7d6b1a403f6`.
The originally recorded artifact 11091906317 is no longer returned by GitHub;
run 36703159302 currently exposes replacement artifact 11104910082. Its archive
digest is recorded separately. The replacement raw SARIF is independently recorded as
`293b9b535ca32e87c1737297414816b4c271bf7123d5070520193814ac8209d0`,
not the historical Cloud digest. Issue #976 already proved all 2,480 original
Deep/R2 Cloud identities and result indices equal, with only retained test lines
moving. The original Deep artifact 11086775183 remains available. Its raw bytes
must match immutable SHA-256
`6f1b59fe7bbcccdd97534e063e2f7205d3e722958f962664cac2352e7b81091b`
and source `64db0f5aaf8b4283360f4c6d7934c8ed89256024`.

Before any frozen index is used, the verifier independently compares every one
of the replay's identities and exact positions with that byte-pinned original
inventory. Missing, new, changed or reordered identities stop proof. The original
Deep digest, historical Cloud digest and replay digest remain distinct in the
proof. No digest, operation count or frozen manifest is rewritten.

`qodana-reconciliation.json` binds the repository, full source revisions,
successful analysis run IDs, artifact IDs and ZIP digests, plus the source packets
whose removals are expected. The workflow rejects stale or failed run metadata,
wrong artifact ownership/digests, empty or multi-run SARIF, wrong source
provenance, unknown packet selections and malformed finding identities.

Identity is rule/path/partial fingerprints/message with exact multiplicity.
Source line movement is permitted; changed messages, paths or fingerprints are
not silently normalized. Every retained identity must remain and every selected
fixed identity must disappear. Unexpected additions, unrelated removals or
missing expected removals stop reconciliation.

Proof artifacts preserve selection, raw baseline/head SARIF, independent raw
digests, download provenance, measured per-rule removals, current rule counts and
the retained inventory digest. The initial selection verifies the nine already
merged packets on main `cb9216cd336b5d36d4a266006398ad31643d6fc8`.
After P11/P13/P14 merge, update only the immutable head evidence and completed
packet selection and repeat proof on the final source main revision.

This workflow changes no application code, dependency, scanner setting, test
oracle or budget. A separate metadata-only PR must later use
`min(current_budget, measured_current_count)` for the affected rules, preserving
zero budgets and historical baseline sourceRevision/totalFindings. Preparation
and proof publication do not themselves complete #976.
