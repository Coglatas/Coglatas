# FCI-05 Files owner acceptance

Issue: #591. Canonical owner: `FUNC-FILE-002` in
`tests/functional/files/files-fast-journey.spec.ts`.

## Execution and evidence repair (Slices A/B/D)

The owner uses `functional-chromium` and fails rather than skipping on an
unsupported project. Fresh FileObject detail reads use the production DTO's
`id`; upload/list/grant responses retain their separate `fileObjectId` contract.
The inspector's Details panel contains metadata, while its header owns the
filename. Sharing labels are compared to the backend's state without assuming
that localized display casing is the wire casing.

The existing P0 acceptance runner now selects the Files owner independently of
its legacy title manifest. `pull_request` selects `functional-fast`; main and
manual acceptance select `functional-full`. No licensed workflow trigger or
credential boundary changes. The regular full Compose runner also retains this
owner. `FUNC-FILE-001` remains the grant reauthorization/security owner.

The Functional reporter fails a selected Files owner with zero results, skips,
expected failures, interruptions, or unsuccessful attempts. Discovery via
`--list` is diagnostic and is not execution evidence. Playwright's normal empty
selection failure is additionally enforced even with `--pass-with-no-tests`.

The test verifies detail/list identity, MIME and byte length, the grant's
FileObject identity, exact downloaded bytes, and the same persisted ID after
reload. Assertions on protected response bodies produce status-only failure
messages. Traces, screenshots, and videos remain disabled. The JSON attachment
contains synthetic names, opaque IDs, statuses, and cleanup results, never
storage keys, paths, grants, tokens, cookies, or file bytes.

Cleanup recovers only the UUID-named object created by this run if upload
committed before response handling failed. A failed cleanup cannot be Green.
The FCI-02/Compose teardown still removes this run's database/storage volumes,
including files intentionally retained by the product's soft-delete policy.

## Verification status

Local Node contracts, actual Playwright reporter pass/skip/empty-selection
regressions, Files discovery, focused TypeScript checking, and the Angular
production build have executed. These do not prove real PostgreSQL/storage
execution. Hosted CI and a protected real-stack run on the final PR commit are
required before acceptance is marked complete.

## Supported lifecycle expansion (Slice C)

The separate lifecycle change adds `F05-FULL-01` through `F05-FULL-04` to the
same `FUNC-FILE-002` owner. An explicit fast selection omits these steps;
full/extended and an unscoped direct journey invocation include them.

- Search the uploaded filename through the real UI and reopen its inspector.
- Create a unique destination through the public folder API, verify fresh
  navigation, move the file through the UI, and verify location/version after
  fresh reads and reload. A stale move leaves the persisted placement unchanged.
- Change Private to Workspace through the sharing UI, verify the exact sharing
  version from fresh sharing/detail/list reads and reload, and reject a stale
  sharing change without overwriting the committed state.
- Verify the real PostgreSQL-triggered initial version, one current pointer,
  metadata/byte consistency, sharing history, and the historical UI preview.
  Deleted-file activity and version content subsequently deny safely.

Folder creation has no product delete route; its run-owned metadata is removed
by isolated database-volume teardown. File bytes intentionally retained by soft
delete are removed by the same run's storage-volume teardown. No shared or
production namespace is used. Folder-filtered browsing, Shared/Starred shortcuts,
rename, restore, and new version upload have no implemented owner contract and
are not claimed as covered. Folder navigation metadata and logical moves are
real, while the file inventory remains the implemented Recent view.

This is source and local contract coverage until an exact-commit real-stack run
passes. Static discovery and PR checks cannot substitute for that execution.
