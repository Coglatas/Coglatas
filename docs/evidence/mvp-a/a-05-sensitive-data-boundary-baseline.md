# A-05 Sensitive Data Boundary Baseline

Issue: A-05 - [MVP-A][P0][DataBoundary] Verify sensitive data, secret, and error leakage boundary baseline

Date: 2026-06-28

Branch: `main`

Base commit at start of verification: `88a13b308ba3ab69fa380ba35135d6e50f80f919`

Result: Needs verification

This sensitive data boundary baseline does not imply production approval, MVP-A Go, production readiness, or acceptance of unrelated MVP-A blockers.

## A-05 Definition

No repo-owned A-05 definition was found before this evidence file was added. The working definition came from the attached issue text supplied for this task: verify that API responses, UI responses, logs, evidence, error handling, config, and exported data do not unintentionally expose secrets, personal data, private school data, or internal implementation details; record direct evidence; keep unverified items as Needs verification or Blocked; do not copy raw secrets, tokens, cookies, session IDs, passwords, connection strings, personal data, or real user data into evidence.

## Environment

| Item | Observed value |
| --- | --- |
| OS | Windows 10.0.26200, win-x64 |
| .NET SDK | 10.0.301 |
| .NET host/runtime | 10.0.9 |
| Docker client | 29.5.3 |
| Docker Compose | v5.1.4 |
| Docker daemon | Not available on `npipe:////./pipe/docker_engine` in this pass |
| Local `gitleaks` executable | Not installed |
| `dotnet user-secrets` | Not configured for `src/AipPortal.Web/AipPortal.Web.csproj` |

## Sensitive Data Classification

| Class | Examples for A-05 | Expected boundary |
| --- | --- | --- |
| Secrets | passwords, tokens, cookies, session IDs, API keys, OAuth client secrets, JWT signing keys, private keys, connection strings | Not committed as real values; not returned from normal responses; not copied into evidence; logged only redacted or omitted. |
| Personal data / PII | real names, emails, student IDs, school affiliation, account identifiers | Use synthetic values in tests/evidence; expose only to authorized actors; avoid new real-user evidence. |
| Private school data | project/conversation/message/file/notification bodies, admin operation details, audit/security metadata | Tenant/resource authorization before response; logs and audit metadata should avoid raw private bodies. |
| Internal implementation data | stack traces, exception details, SQL values, internal paths, host/container names, environment dumps, dependency topology | Generic external errors; no debug dumps in responses or evidence. |

## Scan Scope

| Scope | Result |
| --- | --- |
| Config files | Checked tracked appsettings, Compose files, launch settings, workflow files, `.env.example`, and ignored local `.env` status without copying values. |
| Source | Checked error middleware, logging calls, audit logger redaction, sensitive-setting service behavior, integration setting validation, export repository, DTOs, and controller error shapes. |
| Docs/evidence | Searched existing MVP-A evidence/docs for secret and sensitive-data terms; no raw values were copied into this file. |
| Logs | Source inspection and test output only. Live runtime log capture remains Needs verification. |
| API responses | Source inspection plus automated regression test for unhandled exception responses. Live running-app API matrix remains Needs verification. |
| UI responses | Existing A-03 public-shell evidence reviewed. Authenticated UI response checks remain blocked by P0-001/P0-002. |
| Export/download paths | Source inspection found tenant export repository intentionally excludes sensitive categories; live tenant export/download smoke remains Needs verification. |

## Commands Executed

| Area | Command | Result |
| --- | --- | --- |
| Repo search | `rg -n "A-05|MVP-A|sensitive|secret|leakage|PII|private data|public private|data boundary|error handling|logging|audit|evidence|blocker" docs src tests -S` | No existing A-05 evidence file found before this file; existing MVP-A evidence/docs found. |
| Environment | `dotnet --info` | Passed; SDK 10.0.301 and runtime 10.0.9 observed. |
| Docker version | `docker --version` | Passed with Docker config access warning; client 29.5.3 observed. |
| Docker Compose version | `docker compose version` | Passed; v5.1.4 observed. |
| Docker daemon | `docker info` | Failed; daemon endpoint unavailable. |
| Compose config | `docker compose --env-file .env.example config --quiet` | Passed. |
| User secrets | `dotnet user-secrets list --project src\AipPortal.Web\AipPortal.Web.csproj` | Failed because the project has no `UserSecretsId`; no project user-secrets store was verified. |
| Gitleaks local binary | `gitleaks version` | Failed; command not installed. |
| Keyword scan counts | `git grep -n -I -i` for A-05 terms, counted without printing raw matched values | Completed; see secret scan summary. |
| High-signal secret pattern scan | `git grep -l -I -i -E "BEGIN ... PRIVATE KEY|client_secret|api_key|apikey|AKIA...|ghp_...|xox..." -- .` | One source file matched `apiKey` as a sensitive-key validation literal; no private key/OAuth/GitHub/AWS/Slack token file hit was observed. |
| Local `.env` status | `git ls-files`, `git check-ignore`, and redacted key counting for `.env` | `.env` is ignored/untracked. Sensitive key names are present locally, but raw values were not printed or copied. |
| Error leak regression | `dotnet test tests\AipPortal.Tests\AipPortal.Tests.csproj --configuration Release --filter FullyQualifiedName~GlobalExceptionHandlingMiddlewareTests --logger "console;verbosity=normal" --disable-build-servers` | Initial sandbox run was blocked by NuGet access; approved rerun passed after test assertion correction. |
| Backend suite | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Passed 129/129 after the A-05 error response fix and regression test. |
| Whitespace | `git diff --check` | Passed; command emitted a line-ending warning for the edited middleware file only. |

## Secret Scan Summary

This pass did not copy raw matched values into evidence. Keyword counts are broad and include code identifiers, documentation, tests, placeholders, and dependency lock content where applicable.

| Term | Matches | Files | Notes |
| --- | ---: | ---: | --- |
| password | 274 | 105 | Includes tests, docs, config placeholders, and password-handling code. |
| secret | 174 | 52 | Includes docs, validation code, CI scan configuration, and placeholder wording. |
| token | 3511 | 232 | Broad term; includes auth code, tests, docs, package lock text, and CSRF/API-token identifiers. |
| apikey | 1 | 1 | Source validation literal in integration settings. |
| api_key | 0 | 0 | No hits. |
| connectionstring | 44 | 25 | Includes config/docs/code references. |
| ConnectionStrings | 29 | 23 | Includes appsettings/Compose/docs/code references. |
| BEGIN PRIVATE KEY | 0 | 0 | No hits. |
| client_secret | 0 | 0 | No hits. |
| JWT | 1 | 1 | Documentation/reference term. |
| Cookie | 194 | 64 | Includes cookie configuration, docs, tests, and auth code. |
| Session | 323 | 74 | Includes session model/service/auth tests/docs. |

Summary counts:

- Scanned: tracked repository text via `git grep`, plus ignored local `.env` status/key names without raw values.
- Confirmed committed secret count: 0 in this pass.
- False positive / expected placeholder categories: broad keyword hits in tests, docs, config placeholders, validation literals, and code identifiers.
- Unresolved count: Needs verification for full redacted Gitleaks reproduction because Docker daemon is unavailable and local `gitleaks` is not installed.
- Raw secret values copied into this evidence: no.

## Config Boundary Result

| File area | Result |
| --- | --- |
| `appsettings.json`, `appsettings.Development.json`, `appsettings.Test.json` | Connection strings omit database passwords. No production-like secret value was observed in these tracked files. |
| `appsettings.*.example.json` | Uses environment placeholders for database and object-storage settings. |
| `.env.example` | Contains local-development placeholder/default values only; still must not be reused as production secrets. |
| `.env` | Present locally but ignored/untracked by git. It contains sensitive key names; values were not printed. This is not a committed-secret finding, but local secret safety and rotation status are unknown if the file has been shared outside this workspace. |
| `docker-compose.yml` / on-prem Compose | Requires environment-provided PostgreSQL password. |
| `infra/compose/dev/local.yml` | Has development-only fallback values and local admin defaults; not production-safe. |
| `.github/workflows/ci.yml` | Contains CI-only PostgreSQL/Compose values and runs a redacted Gitleaks scan in CI. |
| `launchSettings.json` | Development environment only; no secret values observed. |

Config result: Pass for committed production-like secret boundary in this local pass; Needs verification for ignored local `.env` value provenance and CI redacted scan artifacts.

## API Error Leakage Result

Before this pass, `GlobalExceptionHandlingMiddleware` returned `exception.Message` when the host environment was Development. That was a source-level error detail leakage risk because exception messages can include connection strings, SQL fragments, paths, or other internal details.

Fix applied:

- `src/AipPortal.Web/Middleware/GlobalExceptionHandlingMiddleware.cs` now returns a generic `InternalServerError` message in every environment.
- `tests/AipPortal.Tests/Auth/GlobalExceptionHandlingMiddlewareTests.cs` verifies a thrown exception containing sensitive-looking text does not expose that text in the JSON response.

Result after fix: Pass for the automated unhandled-exception response regression. Needs verification for a live running-app API matrix across unauthorized, forbidden, validation, DB-failure, admin-only, file/download, and malformed-ID paths.

## Log Leakage Result

Source inspection found:

- `DbAuditLogger` omits metadata keys such as password, token, secret, file path, message body, body, cookie, connection string, and environment variable.
- CSRF rejection logs method and path only, not request body, token value, cookie, or headers.
- Global exception logging records the exception internally with trace ID; external response is now generic.
- No broad HTTP request-body logging middleware or EF sensitive-data logging call was found in this pass.

Result: Partial. No log leakage was observed from the source and test output inspected here, but live startup/request/exception/auth-failure/DB-failure logs were not captured and sanitized in this pass.

## Evidence / Docs Leakage Result

Existing MVP-A evidence files generally avoid copying raw secrets and use redacted language for connection strings, token values, cookies, tenant identifiers, and local credentials. This A-05 evidence also avoids raw values. The broad keyword scan found expected security terms in docs and evidence but did not establish a committed raw secret.

Result: Partial. Existing docs/evidence require a fuller human review before A-05 can be Accepted because broad keyword scans alone cannot prove every historical log excerpt or copied terminal output is clean.

## Private Data Response Boundary Result

Automated backend tests from A-04 plus the full suite cover many private-data boundaries with synthetic data: tenant isolation, admin denial, project/conversation/file/notification scope, audit/security query authorization, hidden login session ID, and sensitive admin setting value omission. A-05 did not add new private-data runtime smoke beyond the error response regression.

Result: Partial / Needs verification. Fresh-runtime authenticated UI/API checks remain blocked by P0-001/P0-002, and export/download runtime checks were not completed.

## Sanitized Examples

| Unsafe evidence form | Safe evidence form used for A-05 |
| --- | --- |
| `Password=<raw value>` | `Password=[REDACTED]` or count/key-name only |
| `Authorization: Bearer <raw token>` | `Authorization header not copied` |
| `Set-Cookie: <raw cookie>` | `cookie value omitted` |
| Full connection string | `connection string redacted` |
| Real email or user name | Synthetic `example.com` or no user value copied |
| Exception detail with path/SQL | Generic error code/message plus trace ID |

## Result

A-05 is Needs verification, not Accepted.

No confirmed committed raw secret or remaining confirmed API error leak was observed after the fix in this pass. The repo still needs live redacted secret-scan artifact verification, live runtime log review, authenticated UI/API private-data smoke, and export/download smoke before A-05 can be considered accepted.

## Limitations

- Docker daemon was unavailable, so the CI-style Dockerized Gitleaks scan could not be reproduced locally.
- Local `gitleaks` was not installed.
- The ignored local `.env` contains sensitive key names; values were intentionally not copied, and provenance/rotation status was not verified.
- `dotnet user-secrets` is not configured for the web project, so no project user-secrets store was verified.
- Live app API/UI/log checks were not fully rerun for A-05.
- Existing P0-001/P0-002 still block fresh-runtime authenticated admin/non-admin/tenant smoke.
- Existing docs/evidence keyword matches need broader manual review before claiming all historical evidence is clean.

## Required Follow-Up

1. Run the CI Gitleaks job or local `gitleaks detect --no-git --redact` in an environment with Docker daemon or a local Gitleaks binary, and review only redacted artifacts.
2. Resolve P0-001/P0-002, then run authenticated admin/non-admin/wrong-tenant API and UI checks without copying cookies, CSRF tokens, credentials, or private bodies.
3. Capture sanitized live startup, request, exception, auth-failure, DB-failure, audit, and security-event logs.
4. Review existing docs/evidence/failure logs for historical raw secret or PII excerpts and mask any confirmed findings.
5. Verify tenant export/download behavior with synthetic data and redacted outputs only.
6. Keep ignored `.env` values out of git and evidence; rotate any value if it was ever shared or committed outside this repo.
