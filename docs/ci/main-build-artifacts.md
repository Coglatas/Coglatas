# Main build artifact fan-out

## Purpose

Main-branch CI must not rebuild the same production application independently in
Performance, real-backend E2E, and image-SBOM workflows.

`.github/workflows/main-build-artifacts.yml` is the trusted main-only `Main CI`
artifact hub. It runs on every `push` to `main` and uses the protected
`syncfusion-licensed-build` environment.

## Build graph

```text
push main
  |
  +-- Main CI
      |
      +-- dotnet restore
      +-- dotnet build Release                 (once)
      +-- package bin/Release + obj
      +-- dotnet publish --no-build            (same build)
      |
      +-- npm ci frontend
      +-- licensed Angular production build    (once)
      |
      +-- assemble production runtime image    (once)
      +-- package runtime image + .NET build
      |
      +--> Qodana Community
      |     +-- restore .NET bin/obj
      |     +-- NuGet restore for runner/container-local packages
      |     +-- skip Qodana bootstrap compilation
      |
      +--> Qodana Cloud
      |     +-- restore the same .NET bin/obj
      |     +-- NuGet restore for runner/container-local packages
      |     +-- skip Qodana bootstrap compilation
      |
      +--> Performance environment
      |     +-- docker load runtime image
      |     +-- restore .NET bin/obj
      |     +-- EF restore + --no-build migration
      |     +-- no app-image rebuild
      |
      +--> Licensed Real Backend Acceptance
      |     +-- docker load runtime image
      |     +-- restore .NET bin/obj
      |     +-- EF restore + --no-build migration
      |     +-- no app-image rebuild
      |     +-- Playwright uses the pinned upstream image directly
      |
      +--> SBOM Image Security
            +-- docker load the same runtime image
            +-- tag the immutable loaded image for the SBOM run
            +-- scan without rebuilding the production image
```

All redistributed artifacts contain or inherit an exact source-SHA stamp.
`scripts/ci/restore-main-build-artifacts.sh` rejects mismatched source or .NET
artifact SHAs before loading or executing redistributed outputs.

## Manual and release fallbacks

The reusable Performance and real-backend workflows retain their existing
manual/integration execution paths. When they are not called by the main artifact
hub, they fall back to their ordinary local build behavior.

SBOM Image Security retains local image construction for manual and release
execution. Only the main-branch reusable path consumes the redistributed runtime
image.

## Deliberate exclusions

### OS portability

OS portability continues to restore and build independently on Linux, Windows,
and macOS. The ability to build successfully on each OS is the evidence being
tested, so substituting a Linux-produced build artifact would invalidate the
test.

### Qodana

Main-push Qodana Community and Qodana Cloud are consumers of the trusted main
artifact hub. They restore the exact-SHA `.NET` `bin/obj` output, perform a
lightweight NuGet restore for their own execution environment, and skip the
duplicate Qodana bootstrap compilation. Manual Qodana workflows remain
standalone fallbacks and perform their own build preparation.

### Path-gated WPC acceptance

The remaining WPC milestone workflows are narrow path-gated acceptance suites.
They are not moved under the always-on main artifact hub because doing so would
increase test execution on unrelated main commits. They remain candidates for a
separate conditional-artifact migration if their automatic main coverage remains
necessary.

## Trust boundary

The artifact hub is not triggered by `pull_request`, `pull_request_target`, or
`workflow_run`. It has no manual or PR trigger. PR ReSharper never participates
in this graph; it exists only in `.github/workflows/ci.yml`, which is pull-request-only. Protected Syncfusion material
therefore executes only against the repository's trusted `main` push revision.

Downstream reusable workflows use the caller revision and same-run artifacts.
They do not accept an arbitrary checkout SHA from workflow inputs.
