# Query boolean OpenAPI correction proposal

Approval status: pending owner confirmation. This file is a proposed metadata change, not applied application code.

Main revision: 2f1b9b711901a84bc425b1a4ce82eff76477e3d7.
Failed CI: https://github.com/NYGsatoshi/Coglatas/actions/runs/36875754649
Schemathesis 4.25.2, alpha-member seed 1708308830, case 11IkF3.

The sanitized case contains Archived as the string "true" and an extra query key whose value is an empty array. Requests omits that empty array on the wire. The request therefore contains only the declared Archived, Search and WorkspaceId parameters. The pinned scanner's additional-properties guard revalidates the already serialized "true" value against a boolean-only JSON Schema and reports it invalid.

Proposed change: in SecurityOpenApiOperationTransformer, expose inline query boolean parameters as type ["boolean", "string"] with the case-insensitive true/false pattern ^(?:[tT][rR][uU][eE]|[fF][aA][lL][sS][eE])$. Leave JSON body schemas, non-query parameters, authorization, validation, persistence and scanner status/check configuration unchanged.

Effect: HTTP request handling remains unchanged. OpenAPI consumers and generated SDKs may expose these query parameters as boolean|string rather than boolean. Native boolean generation remains valid; arbitrary strings, null, numbers and malformed boolean spellings remain outside this schema. Existing query whitespace spellings are not newly advertised.

Validation before merge: fixed-image scanner reproduction using the immutable actual case; negative counterexamples for malformed boolean and integer query values; focused transformer tests; deterministic generated OpenAPI verification; normal PR checks; actual main security runtime after merge.

This needs owner approval because the user's standing instruction requires confirmation before changes that can affect behavior, and generated-client typing is observable.

The generated main contract contains 13 affected inline query boolean parameters:
- GET /api/admin/audit/findings: openOnly, myReviews, overdue, unassigned.
- POST /api/files/selection-snapshots: OnlyMyUploads.
- GET /api/me/tasks and GET /api/me/tasks/counts: Blocked, OnlyOverdue.
- GET /api/projects/{projectId}/kanban: IncludeOlderCompleted.
- GET /api/projects: Archived.
- GET /api/projects/{projectId}/task-labels: includeArchived.
- GET /api/student-records/{studentRecordId}/restricted: includePublic.

The exact proposed source and two focused C# regression tests are in [query-boolean-proposal.patch](query-boolean-proposal.patch). This patch is not applied by this PR or workflow. C# candidate tests have not been compiled or executed; that requires the approved source-change stage.

Pinned-image reproduction succeeded at source eeed29d57299ed8b2b5de9bab060efc719b0b742, run 36884245115, job 110443373484: current guard rejects the captured wire value; the proposed schema correctly recognizes the additional-only case, seven valid values pass, and nine invalid boolean/integer counterexamples stay rejected. Artifact 11172957795 ZIP SHA-256 5175c8c11ced2f759699f85bd9ab9788db6f1c40de89ff733a4d931f1b1343e9. A follow-up reproduction also checks the native-boolean control. This is proposal validation, not evidence that main has been repaired.
