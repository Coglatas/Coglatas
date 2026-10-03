import { buildPlaywrightGrep } from '../../scripts/ci/build-playwright-grep.mjs';
import { verifyPlaywrightRequiredTests } from '../../scripts/ci/verify-playwright-required-tests.mjs';

const manifestUrl = new URL('../../scripts/ci/real-backend-pr-p0-required-tests.txt', import.meta.url);
const specUrl = new URL('./real-backend-smoke.spec.ts', import.meta.url);
const junitUrl = new URL('../../test-results/playwright-results.xml', import.meta.url);

process.env.COGLATAS_REAL_BACKEND_SMOKE_GREP = await buildPlaywrightGrep(manifestUrl, {
  verifyPath: specUrl
});
process.env.COGLATAS_REAL_BACKEND_SMOKE_SCOPE = 'PR P0 required set';
process.env.COGLATAS_REAL_BACKEND_P0_SETUP = '1';
// Keep the existing serialized acceptance runner. PRs execute the bounded
// Owner; trusted main/manual acceptance executes its full lifecycle expansion.
process.env.COGLATAS_FUNCTIONAL_FILES_GATE = process.env.GITHUB_EVENT_NAME === 'pull_request'
  ? 'functional-fast'
  : 'functional-full';

await import('./run-real-backend-smoke-compose.mjs');

if (!process.exitCode) {
  try {
    const result = await verifyPlaywrightRequiredTests(manifestUrl, junitUrl);
    console.log(
      `Real-backend P0 required-test verification passed: ${result.requiredCount} required tests; ${result.discoveredCaseCount} JUnit cases discovered.`
    );
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  }
}
