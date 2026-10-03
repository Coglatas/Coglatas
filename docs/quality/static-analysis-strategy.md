# Static analysis strategy

## Responsibility split

| Tool | Role | Execution |
| --- | --- | --- |
| SonarQube Cloud | Repository-wide quality gate across C#, JavaScript, TypeScript, HTML, CSS and SCSS | Automatic Analysis on every PR update and every push to `main` |
| ESLint + angular-eslint | JavaScript, TypeScript and Angular template policy | `Frontend Static Analysis` on every PR and `main` push; blocking |
| Stylelint | CSS and SCSS policy | `Frontend Static Analysis` on every PR and `main` push; blocking |
| ReSharper InspectCode CLI | Fast JetBrains inspection lane for .NET pull-request feedback | Every PR; runs only when .NET/config inputs changed, scopes ordinary changes to affected projects, reports `WARNING` or higher with solution-wide analysis and duplicate Roslyn analyzer execution disabled, and fails on findings in files changed by the PR |
| Qodana Community for .NET | Deep JetBrains/ReSharper repository inspection and project-model validation | Trusted `main` pushes and manual dispatch; full repository scan with strict Critical/unresolved/project-model guards |
| CodeQL | Security-oriented semantic/data-flow analysis | Every PR targeting `main`, trusted `main` pushes and weekly schedule |

The tools intentionally overlap at the language level but not at the policy level. SonarQube is the primary cross-stack quality view, ESLint/Stylelint enforce frontend-specific rules, CodeQL owns security analysis, ReSharper InspectCode supplies fast pull-request feedback for .NET, and Qodana supplies the deeper trusted-main JetBrains/ReSharper repository lane.

## Frontend lint debt baseline

`Frontend Static Analysis` is blocking, but it does not require unrelated pull requests to eliminate the repository's pre-existing ESLint and Stylelint backlog. `tools/frontend-inspections/baseline.json` records the accepted repository-wide finding count for each lint rule.

Enforce mode fails when the count for any ESLint or Stylelint rule exceeds that committed baseline. Existing findings remain present in the uploaded reports, while each rule's total debt is prevented from growing. The baseline is position-independent; fixing an existing finding can offset a new finding of the same rule elsewhere, so this is a rule-level debt ceiling rather than an exact per-line baseline.

Refreshing the baseline with `node tools/frontend-inspections/run.mjs --update-baseline` is an explicit policy change and should be reviewed as such; it must not be used as an automatic CI escape hatch.

### Boy-scout cleanup policy

Dedicated repository-wide ESLint cleanup phases end after ESLINT-05. From ESLINT-06 onward, lint debt is reduced as a boy-scout cleanup in code that is already being changed for feature, bug-fix, or regression work. A separate cleanup pull request is reserved for a small, explicitly bounded regression fix; it must not become a new repository-wide sweep.

Apply the following constraints:

- Do not run repository-wide autofixes solely to reduce the committed lint baseline.
- Do not increase an ESLint or Stylelint rule count in `tools/frontend-inspections/baseline.json` to make a pull request pass.
- When touched code contains an existing finding that can be removed safely without broadening the behavioral change, remove it in the same pull request.
- Do not introduce `eslint-disable` comments or weaken lint configuration merely to avoid fixing a touched finding.
- If a safe cleanup lowers a rule count, update the baseline downward and review the generated report and baseline diff together.
- If an autofix transfers debt into another rule, expands beyond the touched feature surface, or requires semantic refactoring, leave it for a separately scoped change instead of forcing the fixer through.
- During a regression or feature-freeze gate, keep lint cleanup within the files required for the blocking fix so the validated target SHA is not churned by unrelated cleanup.

This policy preserves the baseline as a monotonic debt ceiling while allowing normal product work to retire findings incrementally.

## SonarQube Cloud mode

This repository uses **SonarQube Cloud Automatic Analysis**, not a token-bearing GitHub Actions scanner.

The repository publication policy forbids secrets in `pull_request` workflows. Automatic Analysis reads the bound GitHub repository directly, so PR analysis does not require `SONAR_TOKEN` in an untrusted PR workflow.

Repository-side scope configuration is stored in `.sonarcloud.properties`.

### One-time SonarQube Cloud setup

1. Import `NYGsatoshi/Coglatas` into SonarQube Cloud through the GitHub integration.
2. In the project, open **Administration > Analysis Method** and enable **Automatic Analysis**.
3. Keep CI-based Sonar scanning disabled for this project; Automatic Analysis and CI-based analysis must not run together.
4. Configure the project Quality Gate for new code.
5. In the GitHub `main` ruleset/branch protection, require the SonarQube Quality Gate status after its first successful report.

Automatic Analysis should then run on each push to `main` and on each update to a pull-request branch.

## Pull-request merge gates

Repository-defined pull-request checks must not gain general repository mutation authority. The CodeQL workflow is the single narrow exception: GitHub's advanced CodeQL setup requires `security-events: write` so SARIF can be published to Code Scanning, while fork `pull_request` runs still receive a read-only `GITHUB_TOKEN` and GitHub explicitly permits Code Scanning result upload for that event.

The PR-stage gates include:

- backend build/test
- frontend build/test
- security scan
- publication readiness
- frontend static analysis (`ESLint` + `Stylelint`)
- ReSharper InspectCode / PR
- CodeQL semantic/data-flow analysis

The SonarQube Quality Gate is supplied by the SonarQube Cloud GitHub integration rather than by a secret-bearing workflow in this repository.

## ReSharper pull-request policy

`.github/workflows/resharper_pr.yml` is the fast .NET inspection lane. It runs on every pull request targeting `main`, but skips the expensive analysis when the diff contains no .NET source, project, solution, SDK, NuGet, ReSharper, or MSBuild configuration inputs.

The workflow pins `JetBrains.ReSharper.GlobalTools` to version `2026.2.2`. Ordinary C# changes are mapped to their owning projects and passed to `InspectCode` with `--project`; repository-wide MSBuild/ReSharper configuration changes fall back to the full solution. The lane restores only the selected project graph, then runs `InspectCode` with `--severity=WARNING`, `--no-swea`, `--no-build`, and `RunAnalyzers=false`. Compile correctness and Roslyn analyzers remain covered by the separate required build/test lane, while trusted `main` Qodana retains the full build-aware deep inspection. The SARIF guard fails only when a reported issue is located in a file changed by the pull request. This keeps historical debt from making unrelated PRs permanently red while still preventing touched files from carrying Warning-or-higher ReSharper findings forward. The full SARIF report is retained as a short-lived workflow artifact. The lane uses read-only repository permissions and no JetBrains license secret.

## Qodana policy

Qodana uses the `qodana.recommended` profile and additionally enables all inspections whose default JetBrains severity is `ERROR`, `WARNING`, or `WEAK WARNING`. Generated output, dependency directories, test artifacts, runtime data and the inactive legacy frontend scaffold remain excluded; first-party source and tests remain in scope.

Qodana is the trusted-main deep lane rather than the pull-request fast lane. `.github/workflows/qodana_code_quality.yml` runs the reusable Community gate on `main` pushes and manual dispatches, while `.github/workflows/qodana_cloud_quality.yml` performs the full trusted Cloud publication on `main`. The temporarily disabled schedules remain disabled.

Historical non-critical debt remains visible rather than making the lane permanently red. The post-processing guard still fails on any Critical finding, any unresolved-symbol finding, project-model/restore/build/SDK/package-resolution failure, missing SARIF output, or Qodana execution failure.
