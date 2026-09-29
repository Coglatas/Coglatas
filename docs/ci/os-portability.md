# COMPAT-02 OS portability matrix

Issue #590 owns toolchain, restore, build, and DB-independent test portability across GitHub-hosted Ubuntu, Windows, and macOS. It does not declare Windows or macOS production deployment support.

## Tested contract

`.github/workflows/os-portability.yml` runs the same bounded contract on:

- `ubuntu-latest`
- `windows-latest`
- `macos-latest`

Each leg uses the SDK selected by `global.json`, Node 24, and the exact npm version declared by the root and active `frontend` manifests. It installs both dependency roots, restores and builds `Coglatas.slnx` in Release, builds the active Angular application in production mode, and runs DB-independent .NET, Angular unit, and frontend helper tests.

The .NET subset is selected only by `Portability=CrossPlatform`. `scripts/ci/os-portability.contract.json` owns the selected classes and minimum result count. `verify-os-portability-results.mjs` rejects missing TRX, a zero or reduced-below-minimum selection, failed tests, skipped/not-executed tests, and incomplete results.

The matrix also reuses COMPAT-04's `os-portability` profile through Playwright discovery. Discovery proves that the shared selection remains resolvable; it does not launch a browser in each OS leg.

## Boundary from other CI dimensions

| Dimension | COMPAT-02 behavior | Owning gate |
| --- | --- | --- |
| Toolchain, restore, compile, DB-independent tests | Runs on all three OSes | `OS Portability Matrix` |
| PostgreSQL migrations, queries, and integration tests | Not multiplied across OSes | Ubuntu `CI / build-test` |
| Chromium, Firefox, and WebKit execution | Not multiplied across OSes | COMPAT-01 / Issue #587 |
| Docker and real-backend browser tests | Not assumed on hosted Windows/macOS | Existing Linux trusted/real-backend gates |
| Screenshot baseline approval | Not performed | Pinned Linux Docker screenshot lane |

The OS portability matrix runs only for relevant `main` pushes (or an explicit manual dispatch), not for pull requests. This keeps the expensive three-OS build/test fan-out out of the PR critical path while preserving post-merge portability evidence. `strategy.fail-fast: false` allows every OS to report its own result; no OS is ignored and the workflow contains no `continue-on-error` escape.

Runner routing uses a self-bounding expression whose only possible outputs are the three declared GitHub-hosted labels. The exact expression and all three labels are registered in `governance/workflow-trust-policy.json`; the portability contract validates that binding before dependency installation.

## Shell, filesystem, and line-ending classification

| Concern | Matrix rule | Repository classification |
| --- | --- | --- |
| Bash, PowerShell, and cmd syntax | Workflow commands call `node`, `npm`, and `dotnet` directly with no explicit shell override; the Node probe invokes the fixed `npm --version` command through `cmd.exe` only for Windows' `.cmd` shim | Existing `.sh` and platform-specific `.ps1` entry points remain owned by their documented platform |
| GNU-only behavior | No `sed`, `grep`, `awk`, `chmod`, or GNU-only flags | GNU-dependent CI/deployment helpers remain Linux-only |
| Temporary paths | Portable tests use `Path.GetTempPath()` or Node path APIs; the workflow contains no `/tmp` | Existing `/tmp` usage remains Linux-lane implementation detail |
| Path separators | Helpers use platform path APIs; repository identifiers normalize to `/` | Selected file-name tests cover `/`, `\`, and drive-prefixed input |
| Executable bits | Helpers are invoked through their runtime | Direct execution of executable-bit-dependent shell scripts remains Linux-only |
| Case sensitivity | Contract validation rejects tracked paths that collide after case folding | Case-only duplicate paths are not allowed |
| LF/CRLF | Portable readers use UTF-8 and CRLF-safe parsing; scripts are not directly executed | Shell scripts are excluded from the Windows/macOS matrix |

## Evidence and failures

Job names include the exact matrix runner label. Every leg writes `artifacts/os-portability/evidence.json` and uploads it with its TRX as `os-portability-<matrix-os>-<run-attempt>`, even after a prior step fails. Evidence contains only run identity, runner/tool versions, fixed step outcomes, test counters, path-collision results, and line-ending classifications; it does not capture environment-variable values, credentials, logs, response bodies, or repository content.

The `latest` runner labels move over time. The evidence therefore records the hosted runner image identity when GitHub exposes it. A failure is investigated against the recorded commit SHA, run attempt, matrix OS, image version, toolchain versions, and first failed step.

## Local checks

The static contract and its regression tests require only Node:

```bash
node scripts/ci/os-portability-contract.mjs --static
node --test tests/ci/os-portability-contract.node-test.mjs
```

`--runtime` additionally requires the repository's exact .NET, Node, and npm toolchains. Local execution on one OS is diagnostic only; acceptance requires all three hosted matrix legs on the same commit.
