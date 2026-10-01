# AGENTS.md

## Scope

These instructions apply to the entire repository unless a more specific `AGENTS.md` exists in a subdirectory.

## Project Language

English is the sole canonical engineering and project language for this repository.
Use English for source identifiers, code comments, docstrings, test names and descriptions, repository documentation, configuration documentation, new commit messages, branch names, Issue/PR titles and bodies, discussion/review comments, project metadata, and AI/code-review output.

Do not create or maintain parallel Japanese documentation, bilingual engineering summaries, or Japanese-only companion artifacts. An instruction received in Japanese does not change the language of the repository artifact you produce.

Follow `docs/PROJECT_LANGUAGE.md` and migration tracker #965. Translate existing non-English engineering records in complete, reviewable batches; preserve requirements, decision status, evidence, references, checkbox states, and issue/PR lifecycle metadata. Record coverage and remaining work rather than claiming an incomplete migration is complete. Do not rewrite published Git history or change product behavior for language cleanup.

Intentional product localization/i18n resources, Japanese IME/Unicode test data, and exact third-party/legal or diagnostic evidence have narrow preservation exceptions documented in the policy. These are not exceptions for engineering prose. Explain preserved non-English evidence in English, retain attribution, and never publish private specification content to satisfy this policy.

## Start Here

Before changing code, read `docs/AI_CONTEXT.md`, `docs/KNOWN_ISSUES.md`, and `docs/CODING_RULES.md`, then read the task-specific documents referenced by `docs/AI_CONTEXT.md`.

Use this authority order when sources conflict:

1. Current source, tests, CI, and root deployment configuration.
2. Active status documentation under `docs/`.
3. `docs/ROADMAP.md` for intended future work.
4. `docs/archive/` as historical context only.

The external specification repository linked from `README.md` defines product requirements. Do not infer that planned or documented behavior is already implemented.

## Repository Shape

- `src/Coglatas.Domain`: entities, enums, value types, and local invariants. No application, infrastructure, ASP.NET Core, or EF Core dependencies.
- `src/Coglatas.Application`: use cases, DTOs, authorization, and service contracts. May depend on Domain.
- `src/Coglatas.Infrastructure`: EF Core/PostgreSQL, repositories, files, audit, search, and adapters. May depend on Application and Domain.
- `src/Coglatas.Web`: composition root, controllers, middleware, authentication, tenant resolution, and hosted frontend artifacts.
- `frontend`: active Angular application and MVP frontend source.
- `coglatas-frontend`: do not assume this is active; modify only when the task establishes that it is required.
- `tests/Coglatas.Tests`: backend unit, service, HTTP, tenancy, and conditional PostgreSQL tests.
- `tests/ui`: Playwright infrastructure for the Angular build.

Keep the product a modular monolith and preserve project-reference direction. Put business logic in Application use cases, not controllers or infrastructure adapters.

## Implementation Rules

- Prefer small, explicit changes matching nearby patterns. Avoid broad abstractions and unrelated refactors.
- Keep controllers thin. Validate requests, call application use cases, and return DTOs rather than EF entities.
- Enforce authorization and tenant/resource scope server-side in every modifying use case. Frontend visibility is not authorization.
- Avoid revealing whether cross-tenant or unauthorized resources exist; follow the established `403`/`404` policy.
- Use async database/file I/O, `AsNoTracking` for read-only EF queries, DTO projections, pagination for large lists, and transactions for multi-record changes.
- Store timestamps in UTC and use the `Utc` suffix for UTC properties.
- Keep migrations focused and reviewable. Add indexes for foreign keys and common filters when schema changes warrant them.
- Keep storage and search behind application interfaces. Read secrets, paths, connection strings, and limits from configuration.
- Never log passwords, tokens, secrets, or file contents. Validate upload size, extension, MIME type, ownership, and authorization.
- Add or update audit records for security-sensitive and important state changes when required by the surrounding workflow.
- Update active docs when architecture, security behavior, module ownership, deployment behavior, or implementation status changes.

## Frontend Rules

- Work in `frontend/` unless the task explicitly targets another UI.
- Follow nearby Angular component, state-management, styling, and test patterns.
- Use `@lucide/angular` for standard interface icons.
- Preserve accessibility, responsive behavior, keyboard operation, loading/empty states, validation, and error handling.
- Do not edit generated hosted artifacts in `src/Coglatas.Web/wwwroot` directly. Use `npm --prefix frontend run build:hosted` when hosted output is required.
- Keep API models aligned with backend DTOs. Mocked Playwright responses are not proof of backend compatibility.

## Testing and Verification

Run the narrowest relevant checks first, then broaden according to risk.

```powershell
dotnet test Coglatas.slnx
npm --prefix frontend test
npm --prefix frontend run build
npm run test:ui
```

Use `npm run test:ui:angular:docker` for authoritative Linux screenshot parity.

Important qualifications:

- PostgreSQL tests require `POSTGRES_TEST_CONNECTION_STRING`; without it they return early and may still be reported as passed.
- EF Core InMemory HTTP tests do not establish PostgreSQL behavior.
- Mocked UI tests do not establish frontend/backend integration.
- Windows/macOS screenshots are diagnostic only; approve baselines with the pinned Linux Docker runner.
- Compose config validation does not prove startup, and configured CI checks do not prove the latest workflow passed.

Add focused regression tests for behavior changes, especially authorization, tenant isolation, persistence, file handling, and frontend/backend contracts. Report which checks ran and all environmental limitations.

## Change Discipline

- Inspect the working tree before editing and preserve unrelated user changes.
- Do not modify generated files, lockfiles, migrations, snapshots, or deployment assets unless required.
- Do not claim a feature is implemented based only on entities, routes, configuration, mocks, or archived plans. Verify the applicable controller, use case, persistence, UI, and tests. Label unexecuted conclusions as inferred or needing verification.
- Keep patches scoped to the requested outcome.
