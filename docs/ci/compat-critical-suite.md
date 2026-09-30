# COMPAT-04 critical compatibility suite

Issue #584 owns the repository-wide selection contract for the small compatibility suite reused by browser-engine, mobile, and OS compatibility CI. The source of truth is `scripts/ci/compat-critical.contract.json`; workflows must select tests through that contract rather than maintain independent handwritten test lists.

## Purpose

`compat-critical` is deliberately smaller than the full Playwright suite. It is intended to answer whether a representative set of browser-facing contracts still works when the runtime environment changes. Functional CI continues to own complete user journeys and real persistence/business behavior; compatibility CI owns representative runtime portability.

The contract requires active, non-quarantined coverage for these categories:

| Category | Compatibility risk represented |
| --- | --- |
| `boot` | production Angular bundle starts and mounts the shell |
| `navigation` | router/direct-route and browser history behavior |
| `form` | browser form input, keyboard submit, and validation/error interaction |
| `overlay-focus` | dialog/drawer open-close, Escape handling, focus containment/return |
| `table-list` | representative list/grid interaction and state updates |
| `responsive` | narrow viewport layout and interaction behavior |
| `touch` | touch-enabled tap activation and absence of hover-only critical controls |
| `horizontal-overflow` | document-wide containment while an explicitly allowlisted component owns bounded horizontal scrolling |
| `fetch-error` | representative fetch/API failure and recovery path |
| `realtime` | minimum real SignalR negotiate/reconnect coverage when the realtime transport is enabled |

A single representative test may satisfy multiple categories when it actually asserts those contracts. Category labels are not claims by themselves: the manifest entry points at the exact existing Playwright test that provides the evidence.

## Stable selection contract

Every entry has a stable logical `id`, exact Playwright `title`, source `file`, coverage `categories`, execution `profiles`, and lifecycle `status`.

Current profiles are:

- `browser-engine`: secretless static Angular subset for desktop Chromium/Firefox/WebKit compatibility lanes. COMPAT-01 (#587) consumes this profile.
- `mobile`: secretless static Angular subset for mobile-engine and explicit narrow-viewport lanes. COMPAT-03 (#594) consumes this profile.
- `os-portability`: deliberately smaller discovery/smoke subset for cross-OS toolchain/build portability. COMPAT-02 (#590) consumes this profile without creating an OS x browser Cartesian product.
- `real-backend`: synthetic local-backend profile containing the minimum real SignalR negotiate/reconnect evidence. It is kept outside the secretless static browser matrix because static Angular runs intentionally default the realtime feature flag off.

Generate the exact-title Playwright grep with:

```bash
node scripts/ci/build-compat-critical-grep.mjs --profile browser-engine
```

List the selected source files with:

```bash
node scripts/ci/build-compat-critical-grep.mjs --profile browser-engine --files
```

Run a profile through the common compatibility runner with Playwright arguments after `--`:

```bash
npm run test:ui:compat-critical -- --profile browser-engine -- --project=chromium-desktop
```

The runner forces `--retries=0`; a retry is not part of the compatibility success definition.

## COMPAT-01 browser-engine matrix

Issue #587 adds `firefox-desktop` and `webkit-desktop` beside the existing
`chromium-desktop` Playwright project. The extra projects are enabled only when
`COGLATAS_COMPAT_CRITICAL=1`; the ordinary full static suite remains on its existing
Chromium desktop/mobile projects. This keeps the engine matrix bounded to the
single COMPAT-04 `browser-engine` selection contract.

`.github/workflows/compat-critical-preflight.yml` keeps the lightweight contract
and Playwright-discovery preflight on pull requests, but runs the profile as three
independent jobs only after a relevant change reaches `main`:

| Job | Playwright project | Browser installed |
| --- | --- | --- |
| `compat-chromium` | `chromium-desktop` | Chromium |
| `compat-firefox` | `firefox-desktop` | Firefox |
| `compat-webkit` | `webkit-desktop` | WebKit |

The matrix uses `fail-fast: false` and has no `continue-on-error`. A Firefox or
WebKit failure therefore remains an explicit failed job even if Chromium
passes, while all three jobs still finish and preserve their own evidence.
Each job uploads its HTML/JUnit/failure attachments under an artifact name
starting with its engine. The common runner validates the manifest and source,
fails when selection is empty or partial, and appends `--retries=0` after all
caller-supplied Playwright arguments.

The pull-request preflight has read-only repository permissions, persists no checkout
credential, and references no protected secret or licensed build activation. The
Chromium/Firefox/WebKit execution matrix is intentionally deferred until `main`.
Selected critical tests may not contain
inline `skip`, `fixme`, or expected-failure exceptions. The only existing
quarantine mechanism is the reviewed contract entry described below, including
its reason, owner, tracking Issue, and expiry. There are currently no
engine-specific exceptions; one cannot be introduced as an unexplained inline
skip.

Playwright WebKit is evidence about this WebKit engine configuration. It is not
evidence of real Safari on Apple hardware, Safari release integration, iOS, or
device-specific behavior, and must not be represented as Safari certification.

## COMPAT-03 mobile execution contract

Issue #594 runs the canonical `mobile` profile in three isolated matrix cells:

| Playwright project | Engine/device contract | CSS viewport |
| --- | --- | --- |
| `chromium-mobile` | existing Pixel 5 emulation on Chromium | `393 x 727` |
| `webkit-mobile` | iPhone 13 emulation on WebKit | `390 x 664` |
| `narrow-320` | explicit Chromium touch context independent of a named device preset | `320 x 800` |

The two additional projects are enabled only when `COGLATAS_COMPAT_CRITICAL=1`, so
the ordinary functional/static suite keeps its established Chromium pair. The
matrix therefore reuses one COMPAT-04 `mobile` selection contract instead of
maintaining a second title list or multiplying the full browser suite.

`.github/workflows/mobile-compatibility.yml` executes this matrix only for relevant
`main` pushes (or an explicit manual dispatch), not for pull requests.

The mobile profile covers shell boot, navigation, representative form/list and
overlay behavior, touch activation, and horizontal-overflow containment. The
touch case uses Playwright `tap()` in a touch-enabled context and completes its
critical flow without a hover action. The overflow case rejects page-wide or
unexpected nested horizontal scrolling at every configured mobile viewport and
separately permits intentional component-scoped horizontal scrolling only in
the allowlisted shared data grid.

Run individual cells with:

```bash
npm run test:ui:compat-critical -- --profile mobile -- --project=chromium-mobile
npm run test:ui:compat-critical -- --profile mobile -- --project=webkit-mobile
npm run test:ui:compat-critical -- --profile mobile -- --project=narrow-320
```

These are secretless static Angular runs with mocked API responses. Playwright
WebKit plus an iPhone descriptor is engine/emulation evidence, not certification
of real iPhone hardware or Apple Safari. Physical-device input, browser chrome,
OS integration, and Safari-specific behavior require separate real-device
validation.

## Fail-closed enforcement

`.github/workflows/compat-critical-preflight.yml` validates the contract on pull requests and on `main`. The validator rejects the contract when any of these conditions occurs:

- the manifest or a profile selects zero tests;
- a required category has no active, non-quarantined representative;
- an `obsolete` or `superseded` test remains attached to a selection profile;
- quarantine removes the last representative for a required profile/category;
- duplicate IDs or exact titles make selection ambiguous;
- a source file or exact Playwright test declaration is missing/renamed;
- a selected test contains arbitrary `waitForTimeout`/`setTimeout` sleeps;
- a selected test directly uses unseeded `Math.random`/`randomUUID` values;
- a selected test is reduced to `toHaveScreenshot`/`toMatchSnapshot` pixel-only evidence;
- a selected test contains an inline `skip`, `fixme`, or expected-failure exception;
- Playwright `--list` discovers zero, partial, duplicate, or unexpected selected tests.

The preflight runs actual Playwright discovery for every profile and all configured browser/mobile projects. Therefore a filename/title rename or a missing project cannot silently turn the compatibility suite into a successful zero-test run.

## Determinism

Compatibility execution sets `TZ=UTC` and `COGLATAS_COMPAT_CRITICAL=1`. `playwright.config.ts` uses the latter to pin the compatibility browser context to a stable locale/timezone/color-scheme/reduced-motion contract without changing unrelated screenshot baselines. Existing acceptance storage state continues to pin the product locale to English.

Selected tests use fixed synthetic fixtures. The source validator rejects direct unseeded randomness and arbitrary sleeps inside selected test bodies. Cross-engine pass/fail is based on behavioral assertions; screenshot comparison may supplement a test but cannot be the only selected compatibility assertion.

## Quarantine policy

Quarantine is an explicit manifest entry, never an unreviewed workflow skip. Each quarantine must include:

- `testId`
- `reason`
- `owner`
- related `issue`
- `expiresOn` in `YYYY-MM-DD`

Expired quarantine fails validation. A quarantined test does not count toward required category coverage, so quarantining the only representative for a profile/category fails closed. Removal or renewal must be an explicit repository change.

## Maintenance procedure

When adding, renaming, replacing, or removing compatibility representatives:

1. Prefer reusing an existing deterministic Playwright assertion rather than duplicating a functional journey.
2. Give a new logical case a stable `COMPAT-...-NNN` ID. Do not reuse an old ID for a semantically different contract.
3. Update the exact title/file/categories/profiles in `scripts/ci/compat-critical.contract.json`.
4. Mark replaced entries `obsolete`/`superseded` and remove them from profiles, or delete the manifest entry when historical tracking is unnecessary.
5. Run `node --test tests/ui/compat-critical-contract.node-test.mjs`.
6. Run `node scripts/ci/verify-compat-critical.mjs --profile <profile> --project <existing-project>` to prove actual Playwright discovery.
7. Run the relevant compatibility execution lane. Do not accept a Green result produced only by retrying a flaky failure.

COMPAT-01/02/03 should consume this selection API/profile contract. They may add browser projects, OS jobs, or mobile profiles, but they should not fork their own lists of critical test titles.
