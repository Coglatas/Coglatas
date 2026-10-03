/// <reference types="node" />

import { defineConfig, devices } from "@playwright/test";

const port = Number(process.env.PLAYWRIGHT_PORT ?? 4173);
const baseURL = process.env.PLAYWRIGHT_BASE_URL ?? `http://127.0.0.1:${port}`;
const skipFrontendBuild = process.env.PLAYWRIGHT_SKIP_BUILD === "1";
const publicHttpsSmoke = process.env.COGLATAS_PUBLIC_HTTPS_SMOKE === "1";
const expectTimeout = publicHttpsSmoke || process.env.COGLATAS_REAL_BACKEND_SMOKE === "1" ? 15_000 : 5_000;
const snapshotPathTemplate = process.env.CI
  ? "{testDir}/__angular_snapshots__/linux/{testFilePath}/{arg}{ext}"
  : "{testDir}/__angular_snapshots__/{testFilePath}/{arg}{ext}";
const compatCriticalRun = process.env.COGLATAS_COMPAT_CRITICAL === "1";
const compatCriticalContext = compatCriticalRun
  ? ({
      locale: "en-US",
      timezoneId: "UTC",
      colorScheme: "light",
      reducedMotion: "reduce"
    } as const)
  : {};

// Issue #361 replaced the embedded two-boolean source-scope smoke with the
// dedicated four-kind tri-state smoke in task-execution-source-policy-v2.spec.ts.
// Keep the obsolete V1 scenario out of the suite rather than asserting a UI
// contract the product no longer exposes; equivalent V2 browser coverage runs
// in both Chromium projects below.
const supersededSourcePolicyV1Smoke =
  /keeps the server-authorized Task execution policy responsive without offering a runtime action$/;

// Issue #363 replaced the embedded #356 Activity placeholder smoke with the
// dedicated authorized Activity/version-history browser smoke. The old test
// intentionally expected no Activity request, which is no longer the product
// contract now that Files exposes a reauthorized File-specific Activity API.
const supersededFileActivityPlaceholderSmoke =
  /keeps one keyboard-accessible File inspector with staged metadata at 320px$/;

const supersededAngularSmokes = new RegExp(
  `${supersededSourcePolicyV1Smoke.source}|${supersededFileActivityPlaceholderSmoke.source}`
);

// Existing Playwright acceptance assertions and screenshot baselines are English.
// Pin that locale explicitly so the product's Japanese default does not rewrite
// unrelated acceptance contracts; locale-specific behavior is tested separately.
const deterministicUiStorageState = {
  cookies: [],
  origins: [
    {
      origin: new URL(baseURL).origin,
      localStorage: [{ name: "coglatas.locale", value: "en" }]
    }
  ]
};

// Keep the regular full static suite on its established Chromium desktop/mobile
// projects. COMPAT-01 enables the additional desktop engines only through the
// small COMPAT-04 selection contract, so adding an engine does not multiply the
// complete browser suite or create a second compatibility taxonomy.
const compatOnlyDesktopProjects = compatCriticalRun
  ? [
      {
        name: "firefox-desktop",
        use: { ...devices["Desktop Firefox"] }
      },
      {
        name: "webkit-desktop",
        use: { ...devices["Desktop Safari"] }
      }
    ]
  : [];

// WebKit mobile and the explicit 320 CSS-pixel contract are compatibility-only
// projects. Keeping them behind the compat-critical runner prevents the normal
// two-project functional suite from becoming a full browser/profile matrix.
const mobileCompatibilityProjects = compatCriticalRun
  ? [
      {
        name: "webkit-mobile",
        use: { ...devices["iPhone 13"] }
      },
      {
        name: "narrow-320",
        use: {
          browserName: "chromium" as const,
          viewport: { width: 320, height: 800 },
          screen: { width: 320, height: 800 },
          deviceScaleFactor: 1,
          isMobile: true,
          hasTouch: true
        }
      }
    ]
  : [];

export default defineConfig({
  testDir: "./tests/ui",
  snapshotPathTemplate,
  timeout: publicHttpsSmoke ? 180_000 : 30_000,
  expect: {
    timeout: expectTimeout
  },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  grepInvert: supersededAngularSmokes,
  // The public deployment gate intentionally emits no trace, video, screenshot,
  // HTML, or JUnit artifact. Browser-network artifacts can contain session or
  // CSRF material even when the test code never logs those values.
  reporter: publicHttpsSmoke
    ? [["list"]]
    : [
        ["list"],
        ["html", { outputFolder: "playwright-report", open: "never" }],
        ["junit", { outputFile: "test-results/playwright-results.xml" }]
      ],
  use: {
    baseURL,
    storageState: deterministicUiStorageState,
    ...compatCriticalContext,
    trace: publicHttpsSmoke ? "off" : "retain-on-failure",
    screenshot: publicHttpsSmoke ? "off" : "only-on-failure",
    video: publicHttpsSmoke ? "off" : "retain-on-failure"
  },
  webServer: process.env.PLAYWRIGHT_BASE_URL
    ? undefined
    : {
        command: skipFrontendBuild
          ? `node tests/ui/serve-static.mjs --port ${port}`
          : `npm --prefix frontend run build && node tests/ui/serve-static.mjs --port ${port}`,
        url: `${baseURL}/health`,
        reuseExistingServer: !process.env.CI,
        timeout: 120_000,
        stdout: "pipe",
        stderr: "pipe"
      },
  projects: [
    {
      name: "chromium-desktop",
      use: { ...devices["Desktop Chrome"] }
    },
    {
      name: "chromium-mobile",
      use: { ...devices["Pixel 5"] }
    },
    ...compatOnlyDesktopProjects,
    ...mobileCompatibilityProjects
  ]
});
