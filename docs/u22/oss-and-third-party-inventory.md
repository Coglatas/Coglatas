# U-22 open-source and third-party inventory

Status: technical inventory for submission preparation, not legal advice or a
complete software bill of materials. Regenerate and review the dependency
trees on the frozen submission SHA before distribution.

## Sources of truth

- Direct JavaScript dependencies: `package.json` and `frontend/package.json`.
- Resolved JavaScript trees and declared package licenses:
  `package-lock.json` and `frontend/package-lock.json`.
- Direct .NET dependencies: project files under `src/` and `tests/`.
- Resolved .NET dependencies: restore assets and `dotnet list package` output
  produced for the frozen SHA.
- Container image provenance: `Dockerfile`, `infra/docker/backend-dev.Dockerfile`,
  `infra/docker/frontend-dev.Dockerfile`, `infra/docker/playwright.Dockerfile`, and the selected Compose
  files.

This document intentionally does not copy every transitive package license out
of lockfiles. That export should be attached to the submission record or
release artifact when required by the contest or distribution channel.

## Direct runtime and development components

| Area | Direct components observed in manifests | Notes |
| --- | --- | --- |
| Backend platform | .NET 10, ASP.NET Core, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.11 | Application host and modular-monolith runtime. |
| Data access | Microsoft.EntityFrameworkCore, Design, and Relational 10.0.11; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 | EF Core with PostgreSQL provider. |
| Backend tests | xUnit 2.9.3, xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk 18.9.0, coverlet.collector 10.0.1, EF Core InMemory 10.0.11 | Test-only packages. |
| Angular frontend | Angular 21.2.x, Angular CDK 21.2.14, RxJS 7.8.2, tslib 2.8.1, zone.js 0.16.2 | Browser application and framework support. |
| Frontend state and realtime | NgRx 21.1.1, Microsoft SignalR 10.0.0 | Browser state and realtime client. |
| UI helpers and grids | Lucide Angular 1.27.0, AG Grid Community/Angular 36.0.2 | Visual controls and icons. |
| Test and browser tooling | Playwright 1.62.0, axe-core 4.12.1, @axe-core/playwright 4.12.1, Vitest 4.1.10, jsdom 28.0.0 | Test-only tooling. |
| Documentation/component tooling | Storybook 10.5.5 and @storybook/angular 10.5.5 | Development and documentation tooling. |
| Build tooling | Angular CLI/build tooling 21.2.19, TypeScript 5.9.3, Prettier 3.9.6 | Development/build tooling. |
| PostgreSQL image | postgres:18-alpine | Development, test, and selected Compose profiles. |
| Build/runtime images | node:24, node:24-alpine, mcr.microsoft.com/dotnet/sdk:10.0.401, mcr.microsoft.com/dotnet/aspnet:10.0.11 | Build and host containers. |
| Browser-test image | mcr.microsoft.com/playwright:v1.62.0-noble | Linux Playwright test container. |

Exact resolved transitive versions may differ from the concise direct list
above. The package locks and frozen restore output remain authoritative.

## Commercial or separately licensed component

The frontend manifest includes Syncfusion EJ2 Angular packages, including
Gantt, Grids, Inputs, and Popups. Syncfusion is a commercial third-party
component family. A valid entitlement and the vendor's current terms must be
confirmed for the exact contest and distribution use. Its license value is a
build-time secret; it must not be committed, printed, embedded in runtime
configuration, or included in submission artifacts.

The repository's license guard is intentionally fail-closed for licensed
builds. The ordinary fallback build is not evidence that a licensed artifact
may be distributed without the required entitlement.

## Final inventory procedure

On the frozen submission SHA, record the output location and checksum of these
commands without adding secrets to their logs:

```powershell
dotnet list Coglatas.slnx package --include-transitive
npm ls --all --json
npm --prefix frontend ls --all --json
```

Also record the exact container image tags or immutable digests used for the
release/demo environment. Review direct and transitive license obligations,
attribution requirements, notices, security advisories, and commercial terms
with the repository owner or qualified reviewer.

## Repository-license note

No root repository `LICENSE` or `NOTICE` file was located while preparing this
inventory. This is not a conclusion about ownership or permission. Before any
distribution beyond the contest's permitted submission terms, the repository
owner should explicitly confirm the source license and any required notices.
