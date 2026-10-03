import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

import FilesOwnerReporter from './files/files-owner-reporter.mjs';

test('Files owner requires an executed pass, including expected-failure and interrupted results', () => {
  for (const [expectedStatus, statuses] of [
    ['skipped', ['skipped']], ['failed', ['failed']], ['passed', []],
    ['passed', ['timedOut']], ['passed', ['interrupted']], ['passed', ['failed', 'passed']],
  ]) {
    const reporter = new FilesOwnerReporter();
    reporter.onBegin({}, { allTests: () => [{
      annotations: [{ type: 'journey', description: 'FUNC-FILE-002' }],
      expectedStatus, results: statuses.map((status) => ({ status })),
    }] });
    assert.deepEqual(reporter.onEnd({ status: 'passed' }), { status: 'failed' });
  }
});

test('actual Playwright exits nonzero for a skipped Files owner and an empty selection', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'fci05-reporter-'));
  try {
    const playwright = new URL('../../node_modules/@playwright/test/index.mjs', import.meta.url).href;
    const reporter = fileURLToPath(new URL('./files/files-owner-reporter.mjs', import.meta.url));
    const config = join(directory, 'playwright.config.mjs');
    await writeFile(config, `export default ${JSON.stringify({ testDir: directory, reporter: [[reporter]] })};`);
    const spec = join(directory, 'owner.spec.mjs');
    for (const [body, exitCode, extraArgs] of [
      ['', 0, []], ['test.skip();', 1, []],
      ['', 0, ['--list']],
      ['', 1, ['--grep', 'no-matching-owner', '--pass-with-no-tests']],
    ]) {
      await writeFile(spec, `import { test } from ${JSON.stringify(playwright)};
        test('Files owner', { annotation: { type: 'journey', description: 'FUNC-FILE-002' } }, () => { ${body} });`);
      const result = spawnSync(process.execPath, [
        fileURLToPath(new URL('../../node_modules/@playwright/test/cli.js', import.meta.url)),
        'test', '--config', config, ...extraArgs,
      ], { encoding: 'utf8', timeout: 30_000 });
      assert.equal(result.status, exitCode, result.stdout + result.stderr);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
