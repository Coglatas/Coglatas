# U-22 synthetic demo data

Status: Test-only, deterministic presentation fixture. This is not production
seed data or a deployment profile.

## Safety boundary

`AppDbContextSeed.SeedBrowserSmokeAsync` runs the fixture only after the host
has confirmed both of these conditions:

- `ASPNETCORE_ENVIRONMENT=Test`; and
- an explicit browser-smoke seed opt-in (`COGLATAS_BROWSER_SMOKE_SEED_ENABLED=true`
  or `BrowserSmokeSeed:Enabled=true`).

This U-22 overlay uses `COGLATAS_BROWSER_SMOKE_SEED_ENABLED=true`.

The host rejects that opt-in in Development, Production, and every other
environment. The manual overlay below exposes only the application on
`127.0.0.1`; it does not publish PostgreSQL. It also disables the smoke
response-gate endpoints that browser tests use.

Never add these deterministic credentials or this Compose overlay to a shared,
deployed, or production environment. No production seed path creates this
data.

## Fixture contents

The fixture uses the existing synthetic browser-smoke account and Workspace.
The U-22-specific records are idempotent by the Project slug and Task title:

| Record | Fixed value |
| --- | --- |
| Project | `U-22 Synthetic Demo Project` (`u22-synthetic-demo-project`) |
| Task | `U-22 Synthetic Demo Task` |
| Goal | Demonstrate a secure, repeatable U-22 Task workflow. |
| Deliverable | A concise walkthrough of the Task Brief, source policy, and current Task state. |
| Constraints | Explicitly says that this is synthetic Test data and has no Web retrieval, provider, runtime, raw-source, or execution claim. |
| Current Task state / phase | `InProgress` / configured `In progress` Stage; it keeps legacy numeric progress at `0`. |
| Project default source policy | Web disabled; Project files eligible as a future source. |
| Task policy | Complete Task override: Web eligible; Project files disabled. |
| Activity | One fixed `Note` at `2026-08-20T09:00:00Z`, labelled as synthetic presentation data rather than execution or phase-transition history. |

The policy flags do not retrieve Web material, list files, persist source
content, select a provider, start a runtime, or create a Task execution run.
The Task detail therefore remains truthful: it can show the current policy,
phase, and existing Activity, but it cannot claim that a source was consumed or
that work was executed.

## Repeatable loopback-only demo stack

Set a valid local Syncfusion build license through an environment variable; do
not put its value in this repository. Then start only the app and its declared
dependencies from the isolated real-backend stack:

```powershell
$env:SYNCFUSION_LICENSE = '<local license value>'
docker compose -p coglatas-u22-demo `
  -f infra/compose/test/real-backend-smoke.yml `
  -f infra/compose/demo/u22.yml `
  up --build --wait app
```

Open [the loopback login page](http://127.0.0.1:8088/app/login) and use the
test-only account configured by the base stack:

```text
Email: e2e-user@example.test
Password: E2eSmoke!23456
```

The data is refreshed idempotently when the app starts. During the demo, open
the U-22 synthetic Project and Task to show the persisted Brief, the configured
current phase, the source-policy difference, and the single synthetic Activity
record. Do not describe the policy as a completed Web search, file read, or
execution.

Tear the isolated stack and its test volumes down afterwards:

```powershell
docker compose -p coglatas-u22-demo `
  -f infra/compose/test/real-backend-smoke.yml `
  -f infra/compose/demo/u22.yml `
  down --volumes --remove-orphans
```

Before starting it, validate the composed configuration without running
containers:

```powershell
docker compose -p coglatas-u22-demo `
  -f infra/compose/test/real-backend-smoke.yml `
  -f infra/compose/demo/u22.yml `
  config --quiet
```
