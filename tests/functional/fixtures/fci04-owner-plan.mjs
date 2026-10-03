import { buildFunctionalGrep } from '../../../scripts/ci/build-functional-grep.mjs';

export function buildFci04OwnerPlan(rawGates = '', ownerOnly = false) {
  const gates = rawGates.split(',').filter(Boolean);
  if (gates.some((gate) => !['functional-fast', 'functional-full'].includes(gate))) {
    throw new Error('FCI-04 requires explicit functional-fast/functional-full gates.');
  }
  if (ownerOnly && gates.length === 0) {
    throw new Error('FCI-04 cannot report success with zero selected owner gates.');
  }
  return [...new Set(gates)].map((gate) => ({
    name: `required FUNC-TASK-001 owner at ${gate}`,
    args: [
      '--config', 'playwright.functional.config.ts',
      'project-task/core-golden-journey.spec.ts',
      '--grep', buildFunctionalGrep({ gates: [gate], journeys: ['FUNC-TASK-001'], backends: ['real'] }),
      '--project=functional-chromium', '--workers=1', '--retries=0',
    ],
    environment: {
      COGLATAS_FCI04_REQUIRED: '1',
      COGLATAS_FUNCTIONAL_SELECTED_GATES: gate,
    },
  }));
}
