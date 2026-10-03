import { buildPlaywrightGrep } from '../../scripts/ci/build-playwright-grep.mjs';
import { fci04P0Gates } from '../functional/fixtures/fci04-owner-plan.mjs';
import { verifyPlaywrightRequiredTests } from '../../scripts/ci/verify-playwright-required-tests.mjs';

const manifestUrl = new URL('../../scripts/ci/real-backend-pr-p0-required-tests.txt', import.meta.url);
const specUrl = new URL('./real-backend-smoke.spec.ts', import.meta.url);
const junitUrl = new URL('../../test-results/playwright-results.xml', import.meta.url);

process.env.COGLATAS_REAL_BACKEND_SMOKE_GREP = await buildPlaywrightGrep(manifestUrl, {
  verifyPath: specUrl
});
process.env.COGLATAS_REAL_BACKEND_SMOKE_SCOPE = 'PR P0 required set';
process.env.COGLATAS_REAL_BACKEND_P0_SETUP = '1';
process.env.COGLATAS_FCI04_GATES = fci04P0Gates(process.env.COGLATAS_FCI04_GATES);

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
