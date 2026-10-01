# MVP-A P0 Frontend CI Stages

> Functional CI taxonomy, stable journey ownership, and PR/main/nightly/release
> gate semantics are canonicalized under
> `docs/verification/functional-ci/`. This file describes frontend-specific CI
> stages and screenshot handling; when classification or Functional gate
> ownership is in question, the FCI-01 documents are the source of truth.

Angular under `frontend/` is the only supported MVP-A P0 frontend implementation.
The repository root `package.json` is retained for repo-level Playwright/E2E
infrastructure only.

## Current CI Gate

1. Backend restore, build, migrations, and tests stay in the existing `.NET`
   CI job and are not weakened by the frontend gate.
2. Root `npm ci` installs the repo-level Playwright runner.
3. `frontend npm ci` installs the Angular workspace dependencies.
4. `frontend npm run build` verifies the production Angular build.
5. `frontend npm run test` runs the configured Angular unit test command
   (currently Angular's Vitest-backed `ng test --watch=false`).
6. `frontend npm run build-storybook` verifies Storybook can build from the
   Angular component source.
7. Root `npm run test:ui:angular:docker` runs the Angular Playwright smoke in
   the pinned Linux Playwright image from `infra/compose/dev/playwright.yml`.

## Staged Playwright Plan

- Stage 1: Angular build/static shell smoke in the normal frontend CI path.
- Stage 2: focused Angular mock/intercept coverage for responsive, accessibility,
  focus, error-state, and UI regression. Under the Functional CI policy this is
  focused regression, not real-functional journey ownership when core APIs are
  intercepted.
- Stage 3: ASP.NET Core + production Angular + migrated PostgreSQL real-backend
  acceptance. This now exists in the Compose-backed real-backend/MBJ/authz
  suites and is currently executed on `main`/manual protected acceptance. FCI-08
  and FCI-09 own promoting the stable journey subsets into the canonical
  `functional-fast` and `functional-full` topology.

See:

- `docs/verification/functional-ci/functional-test-policy.md`
- `docs/verification/functional-ci/functional-journey-matrix.md`
- `docs/verification/functional-ci/functional-gate-topology.md`

## Screenshot Regression Status

Blocking Angular-approved screenshot baselines exist for the P0 desktop shell
and mobile shell under
`tests/ui/__angular_snapshots__/angular-smoke.spec.ts/`. CI and local baseline
verification must use `npm run test:ui:angular:docker`, which pins the Linux
Playwright browser/runtime to the repository's locked `@playwright/test`
version. Do not approve screenshot baselines generated from Windows or macOS
host-native Playwright runs.
GitHub Actions screenshot failures remain Conditional Go until the same
regression passes again in this Linux Playwright environment.

Screenshot failures are blocking for hidden action exposure, hidden route
exposure, unusable primary navigation, permission/session screen breakage,
messaging composer/file upload/admin grid unusability, or major shell layout
collapse.

Screenshot/visual regression remains a separate classification from real
Functional coverage. A screenshot pass must not be used to satisfy a real
journey owner slot in the Functional CI matrix.

## Legacy Static SPA Handling

Legacy static SPA routes, DOM selectors, mocked route behavior, and screenshot
baselines from `src/AipPortal.Web/wwwroot` are not MVP-A P0 requirements. The
remaining legacy Playwright placeholder is skipped and labeled non-P0. New
Playwright screenshot assertions, if added later, must use the
`tests/ui/__angular_snapshots__/` path configured by `playwright.config.ts` and
must be reviewed as Angular-approved baselines generated in the Docker
Playwright environment.

`/api/*` paths are not Angular routes. The static Playwright server returns JSON
404 responses for unknown `/api/*` paths instead of falling them back to
`index.html`.
