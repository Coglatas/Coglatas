/// <reference types="node" />

import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.PLAYWRIGHT_BASE_URL ?? 'http://127.0.0.1:5080';

const deterministicUiStorageState = {
  cookies: [],
  origins: [
    {
      origin: new URL(baseURL).origin,
      localStorage: [{ name: 'coglatas.locale', value: 'en' }]
    }
  ]
};

export default defineConfig({
  testDir: './tests/functional',
  testMatch: '**/*.spec.ts',
  timeout: 60_000,
  expect: {
    timeout: 15_000
  },
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  workers: 1,
  reporter: [
    ['./tests/functional/files/files-owner-reporter.mjs'],
    ['list', { printSteps: true }],
    ['junit', { outputFile: 'test-results/functional-playwright-results.xml' }]
  ],
  use: {
    baseURL,
    storageState: deterministicUiStorageState,
    // FCI-09 owns sanitized failure diagnostics. Keep high-risk network/session
    // artifacts disabled by default until that policy is wired.
    trace: 'off',
    screenshot: 'off',
    video: 'off'
  },
  projects: [
    {
      name: 'functional-chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ]
});
