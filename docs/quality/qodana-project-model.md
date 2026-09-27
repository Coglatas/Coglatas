# Qodana project model

Last updated: 2026-09-27.

## Canonical roots

- Repository root: `.`.
- Backend solution: `Coglatas.slnx`.
- Backend projects:
  - `src/Coglatas.Domain/Coglatas.Domain.csproj`
  - `src/Coglatas.Application/Coglatas.Application.csproj`
  - `src/Coglatas.Infrastructure/Coglatas.Infrastructure.csproj`
  - `src/Coglatas.Web/Coglatas.Web.csproj`
  - `tests/Coglatas.Tests/Coglatas.Tests.csproj`
- Active Angular workspace: `frontend/`.
- Legacy Angular scaffold: `coglatas-frontend/`; it is inactive and excluded from Qodana analysis.

## Required toolchain

- .NET SDK: `10.0.400`, pinned by `global.json` with roll-forward disabled.
- Target framework: `net10.0`.
- Node.js: `24.x` for the SARIF/project-model guard.
- Qodana action: `JetBrains/qodana-action` v2026.2.1, pinned to commit `10be11607eb323a180e2b76b26c9c5cdceac3e77`.
- Community linter image: `jetbrains/qodana-cdnet:2026.2-privileged@sha256:21bbbfeac0e61fe8790cc27d5754b87d57b8032c0c32f84ddeb887027f83ec4f`.
- Reusable Qodana gate: `.github/workflows/qodana_trusted_gate.yml` pinned through main-reachable commit `e03555c4be3700aa23f6e0bfe86b740b042dd5fa`.

Qodana Community for .NET is intentionally the .NET lane. Frontend policy is enforced independently by SonarQube Cloud, ESLint/angular-eslint and Stylelint, so the Qodana bootstrap sets `QODANA_SKIP_FRONTEND_BOOTSTRAP=true` in CI instead of spending Community-linter time building an unsupported frontend analysis surface.

## Inspection policy

`qodana.yaml` starts from `qodana.recommended` and additionally enables every inspection whose default JetBrains IDE severity is:

- `ERROR`
- `WARNING`
- `WEAK WARNING`

This keeps the profile deliberately strict while avoiding the previous unrestricted `ALL` inventory, which also enabled low-value typo/information-only inspections and could add substantial noise and scan cost.

Generated output, dependencies, runtime data, test artifacts and the inactive legacy frontend remain excluded. First-party backend source and tests remain in scope.

## Solution and configuration

The canonical .NET project model is configured in `qodana.yaml`:

```yaml
dotnet:
  solution: Coglatas.slnx
  configuration: Release
```

Keeping these values in `qodana.yaml` makes local Docker runs and GitHub Actions consume the same solution/configuration instead of duplicating command-line flags.

## Bootstrap sequence

Qodana runs `scripts/quality/qodana-bootstrap.sh` before inspections. For the Community .NET CI lane the script:

1. Reads the required SDK from `global.json`.
2. Installs that exact SDK if the image does not provide it.
3. Prints the active SDK/MSBuild information.
4. Runs `dotnet restore Coglatas.slnx --verbosity normal`.
5. Runs `dotnet build Coglatas.slnx --configuration Release --no-restore`.
6. Skips the frontend bootstrap because `qodana-cdnet` does not analyze the active Angular/TypeScript application.

Restore, build, SDK, package-resolution, solution-load and project-model failures are hard failures.

## Pull-request quality gate

`.github/workflows/qodana_code_quality.yml` retains the established caller for pull requests, `main` pushes, the weekly schedule and manual dispatch. It delegates analysis to the immutable reusable workflow `qodana_trusted_gate.yml`. The caller now pins the same reviewed gate blob through the main-reachable #724 commit instead of the former intermediate PR commit, so GitHub Actions can resolve the reusable workflow while the reviewed implementation remains unchanged. This existing lane remains tokenless; Qodana Cloud credentials are never passed to it.

For PRs:

- `pr-mode: true` supplies Qodana with pull-request comparison context while retaining the inspection inventory used by the repository guard.
- Before Qodana starts, the workflow writes the exact `base...HEAD` changed-file set to a runner-temporary, NUL-delimited file.
- After Qodana succeeds, `check-qodana-project-model.mjs` compares the current inspection counts with `scripts/quality/qodana-rule-baseline.json`.
- Existing findings are treated as technical-debt budget, not as a reason to fail merely because their file was touched.
- A rule fails the gate when its current count exceeds its recorded budget; a previously unseen rule therefore has an implicit budget of zero.
- The native total-problem `--fail-threshold` is not used because it cannot express this repository's per-inspection ratchet policy.
- Qodana execution failure is not masked with `continue-on-error`.
- Repository permissions remain `contents: read`.
- Qodana comments, annotations and quick-fix pushes are disabled.
- `QODANA_TOKEN` is never passed to the pull-request workflow, including repository-owner PRs.
- Repository-owner PRs may exercise proposed Qodana policy changes directly.
- For every other PR, `qodana.yaml`, the Qodana bootstrap/guard, and the repository helper scripts executed by this job are restored from the PR base SHA before execution; the guard is restored again after analysis before it consumes SARIF.

This preserves analysis of submitted source while preventing an external PR from replacing either the quality-policy scripts or the debt baseline that enforce the result. Historical findings remain visible without making a cleanup PR fail solely because it touched a file that already contained debt.

## Full-repository quality gate and Qodana Cloud

`.github/workflows/qodana_cloud_quality.yml` is a separate trusted Cloud-publishing workflow. It has no `pull_request` or review trigger and runs only for:

- pushes to `main`;
- the weekly schedule;
- manual dispatches on `main`.

The Cloud workflow performs a full repository analysis with `pr-mode: false`, passes `QODANA_TOKEN` directly to the pinned Qodana action, and therefore publishes the report to Qodana Cloud. Manual dispatches on non-`main` refs are blocked by the job-level ref guard.

Because repository publication policy requires every secret-bearing job to use a static protected environment, the Cloud job is bound to the repository's existing `syncfusion-licensed-build` protected environment. Qodana does not consume the Syncfusion secret; the environment is reused solely as the already-established trusted secret boundary. A dedicated Qodana environment may replace it later if one is created with equivalent protection.

The Cloud lane fails before Qodana starts if `QODANA_TOKEN` is empty. The token must be available to the job as the Qodana Cloud project token for this repository. This prevents a green trusted run from silently producing only a local/GitHub report while Qodana Cloud receives nothing.

Keeping Cloud publication in a workflow with no PR trigger is deliberate: Qodana Cloud credentials never enter the pull-request trust boundary, while the existing immutable PR gate remains unprivileged.

The repository currently has historical non-critical Qodana debt. The baseline captured from main commit `c6aedb95c8780a5e8fac42b7b96ecccf80f1ad80` contains 3,479 findings. Instead of accepting unlimited historical debt or requiring an immediate zero-warning migration, the SARIF guard uses a per-inspection ratchet while preserving the full report:

- Critical findings: `0` allowed.
- Unresolved-symbol findings: `0` allowed.
- Files affected by unresolved-symbol findings: `0` allowed.
- Project-model/restore/build/SDK/package-resolution failures: `0` allowed.
- Missing or invalid SARIF: hard failure.
- Qodana process failure: hard failure.
- Missing `QODANA_TOKEN` on a trusted Cloud run: hard failure.
- Any inspection count above `scripts/quality/qodana-rule-baseline.json`: hard failure.
- A newly appearing inspection ID has an implicit baseline of zero and therefore fails until the underlying issue is fixed or an explicitly reviewed baseline increase is approved.

The largest baseline buckets at introduction are `NotAccessedPositionalProperty.Global` (1,173), `PropertyCanBeMadeInitOnly.Global` (595), `InconsistentNaming` (154), `MergeIntoPattern` (150), `UnusedMember.Global` (142), `MemberCanBePrivate.Local` (140), and `AccessToDisposedClosure` (121).

Debt reduction is ordered by risk rather than raw count: fix lifetime/disposal, nullability, short-lived HTTP clients and multiple-enumeration findings first; then apply mechanical cleanup; finally audit DTO/record positional-property and naming findings where serialization, OpenAPI, EF, or other public contracts may make apparently-unused members intentional.

Use `scripts/quality/write-qodana-rule-baseline.mjs` after a successful scan to generate a lower candidate baseline. By default the generator refuses to increase any rule budget; `QODANA_ALLOW_BASELINE_INCREASE=1` is required for an intentional increase so that it is visible in review.

All non-critical findings remain visible in the GitHub Actions inventory artifact, and trusted Cloud runs publish the full-repository result to Qodana Cloud. The existing tokenless full-repository lane remains in place for compatibility with the established immutable gate and repository policy; the Cloud lane adds publication without expanding the PR secret boundary.

## Exclusion rationale

Qodana analyzes first-party source and tests, but not generated output, dependencies, runtime data or inactive source.

Configured exclusions include:

- `**/bin/**`, `**/obj/**`: build/compiler output.
- `**/node_modules/**`: external npm dependencies.
- `**/dist/**`, `**/.angular/**`, `**/storybook-static/**`: frontend generated output/cache.
- `**/coverage/**`, `**/TestResults/**`, `**/test-results/**`, `**/playwright-report/**`, `**/.playwright/**`: test/browser output.
- `**/.qodana/**`, `.tmp`, `**/artifacts/**`: scanner and CI/local artifacts.
- `src/Coglatas.Web/wwwroot`: hosted frontend build output; source of truth is `frontend/`.
- `src/Coglatas.Web/data`: local runtime data.
- `coglatas-frontend`: inactive legacy frontend scaffold.

Tests are not excluded.

## Local reproduction

Backend preparation:

```powershell
dotnet restore Coglatas.slnx
dotnet build Coglatas.slnx --configuration Release --no-restore
```

Run the pinned Community linter image from the repository root:

```powershell
docker run --rm `
  -v "${PWD}:/data/project" `
  -v "${PWD}/.qodana/cache:/data/cache" `
  -v "${PWD}/.qodana/results:/data/results" `
  jetbrains/qodana-cdnet:2026.2-privileged@sha256:21bbbfeac0e61fe8790cc27d5754b87d57b8032c0c32f84ddeb887027f83ec4f
```

Validate the generated SARIF/project model:

```powershell
node scripts/quality/check-qodana-project-model.mjs .qodana/results/qodana.sarif.json
```
