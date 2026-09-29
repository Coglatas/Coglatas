import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const configPath = 'playwright.config.ts';
const runnerPath = 'scripts/ci/run-compat-critical.mjs';
const workflowPath = '.github/workflows/compat-critical-preflight.yml';

const [config, runner, workflow] = await Promise.all([
  readFile(configPath, 'utf8'),
  readFile(runnerPath, 'utf8'),
  readFile(workflowPath, 'utf8')
]);

test('COMPAT-01 defines opt-in Chromium, Firefox, and WebKit desktop projects', () => {
  assert.match(config, /const compatOnlyDesktopProjects = compatCriticalRun/u);
  assert.match(config, /name: "chromium-desktop"/u);
  assert.match(config, /name: "firefox-desktop"[\s\S]*devices\["Desktop Firefox"\]/u);
  assert.match(config, /name: "webkit-desktop"[\s\S]*devices\["Desktop Safari"\]/u);
  assert.match(config, /\.\.\.compatOnlyDesktopProjects/u);
});

test('COMPAT-01 main matrix isolates every engine and uses one critical profile', () => {
  const entries = [
    ['chromium', 'chromium-desktop', 'chromium'],
    ['firefox', 'firefox-desktop', 'firefox'],
    ['webkit', 'webkit-desktop', 'webkit']
  ];

  for (const [engine, project, browser] of entries) {
    const entry = new RegExp(
      `- engine: ${engine}\\s+project: ${project}\\s+browser: ${browser}`,
      'u'
    );
    assert.match(workflow, entry);
  }

  assert.match(
    workflow,
    /compat-browser-engine:\s+if: github\.event_name == 'push' && github\.ref == 'refs\/heads\/main'/u
  );
  assert.match(workflow, /fail-fast: false/u);
  assert.match(workflow, /name: compat-\$\{\{ matrix\.engine \}\}/u);
  assert.match(
    workflow,
    /npm run test:ui:compat-critical -- --profile browser-engine -- --project=\$\{\{ matrix\.project \}\}/u
  );
  assert.doesNotMatch(workflow, /continue-on-error:/u);
});

test('COMPAT-01 remains secretless, retry-free, and emits engine-scoped evidence', () => {
  assert.match(workflow, /permissions:\s+contents: read/u);
  assert.doesNotMatch(workflow, /\bsecrets\./u);
  assert.doesNotMatch(workflow, /^\s*environment:/mu);
  assert.match(workflow, /if: always\(\)/u);
  assert.match(
    workflow,
    /name: compat-\$\{\{ matrix\.engine \}\}-\$\{\{ github\.run_id \}\}-\$\{\{ github\.run_attempt \}\}/u
  );

  const passthroughPosition = runner.indexOf('...passthrough');
  const retryPosition = runner.indexOf("'--retries=0'");
  assert.ok(runner.includes('...passthrough'), 'compat runner must forward explicit Playwright arguments');
  assert.ok(retryPosition > passthroughPosition, 'the final runner argument must force retries=0');
  assert.match(runner, /COGLATAS_COMPAT_CRITICAL: '1'/u);
});

test('browser-facing changes trigger the compatibility matrix', () => {
  assert.match(workflow, /- "frontend\/\*\*"/u);
  assert.match(workflow, /- "tests\/ui\/\*\*"/u);
  assert.match(workflow, /- "playwright\.config\.ts"/u);
  assert.match(workflow, /- "scripts\/ci\/compat-critical\*"/u);
});
