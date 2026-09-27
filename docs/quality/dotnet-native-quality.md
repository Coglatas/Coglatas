# .NET-native quality foundation

Coglatas is moving its maintained application and test tooling toward a .NET-first stack.
This document records the quality controls that can be adopted before the Avalonia UI exists.

## Scope of this stage

This stage deliberately does not introduce Avalonia packages or migrate the existing xUnit 2
suite to xUnit 3 / Microsoft.Testing.Platform. The existing suite is large and remains the
authoritative regression set while the lower-risk quality controls are introduced first.

The stage adds:

- ArchUnitNET architecture tests, distributed as NuGet packages;
- Coverlet collection through the existing `coverlet.collector` NuGet dependency;
- ReportGenerator as a repository-local .NET tool restored from NuGet;
- GitHub Actions coverage summaries and artifacts generated without Node.js.

## Architecture contract

The initial dependency rules mirror the current project references:

- Domain must not depend on Application, Infrastructure, or Web.
- Application must not depend on Infrastructure or Web.
- Infrastructure must not depend on Web.

The rules are intentionally small. They establish a ratchetable architecture boundary without
trying to encode future Avalonia-specific layering before those projects exist.

## Coverage policy

Coverage is collected from the normal PostgreSQL-backed CI test lane. ReportGenerator publishes
a GitHub job summary and an artifact.

This stage does not fail the build on a percentage threshold. A threshold should be introduced
only after a trustworthy baseline has been measured on main, so existing debt is not mistaken
for a regression.

## Follow-up stages

After this foundation is stable:

1. migrate the test runner to xUnit v3 + Microsoft.Testing.Platform v2;
2. add a coverage ratchet or changed-code gate from a measured baseline;
3. add Avalonia.Headless tests when the Avalonia projects exist;
4. add UI-specific architecture rules for UI.Core and platform adapters.
