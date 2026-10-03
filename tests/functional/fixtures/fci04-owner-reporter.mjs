import { mkdirSync, writeFileSync } from 'node:fs';

export const FCI04_JOURNEY = 'FUNC-TASK-001';

export function requiredFci04Steps(gate) {
  if (!['functional-fast', 'functional-full'].includes(gate)) {
    throw new Error(`FCI-04 requires one explicit fast/full gate; received ${gate || '<empty>'}.`);
  }
  return Array.from({ length: 11 }, (unused, index) => index + 1)
    .filter((step) => gate === 'functional-full' || ![3, 4].includes(step))
    .map((step) => `${FCI04_JOURNEY} / STEP-${String(step).padStart(2, '0')}`);
}

export function validateFci04Owner(discovered, records, gate) {
  const requiredSteps = requiredFci04Steps(gate);
  if (discovered.length !== 1 || records.length !== 1) {
    throw new Error(`${FCI04_JOURNEY}: expected exactly one owner and one attempt; discovered=${discovered.length}, executed=${records.length}.`);
  }
  const [owner] = discovered;
  const [record] = records;
  if (owner.journey !== FCI04_JOURNEY || owner.backend !== 'real' || !owner.gates?.split(',').includes(gate)) {
    throw new Error(`${FCI04_JOURNEY}: required real-backend owner metadata is missing.`);
  }
  if (record.status !== 'passed' || record.retry !== 0) {
    throw new Error(`${FCI04_JOURNEY}: owner did not pass its first attempt (${record.status}, retry=${record.retry}).`);
  }
  for (const step of requiredSteps) {
    if (!record.steps.some((completed) => completed === step || completed.startsWith(`${step} `))) {
      throw new Error(`${step}: required owner step did not complete.`);
    }
  }
  return { journeyId: FCI04_JOURNEY, gate, status: 'PASS', completedSteps: record.steps };
}

/** Reject missing/skipped owners even when Playwright itself exits successfully. */
export default class Fci04OwnerReporter {
  discovered = [];
  records = [];

  onBegin(_config, suite) {
    this.discovered = suite.allTests().map((owner) => ({
      journey: owner.annotations.find((annotation) => annotation.type === 'journey')?.description,
      backend: owner.annotations.find((annotation) => annotation.type === 'backend')?.description,
      gates: owner.annotations.find((annotation) => annotation.type === 'functional-gates')?.description,
    }));
  }

  onTestEnd(_owner, result) {
    const steps = result.steps
      .filter((step) => step.category === 'test.step' && !step.error)
      .map((step) => step.title);
    this.records.push({ status: result.status, retry: result.retry, steps });
  }

  onEnd(result) {
    const gate = process.env.COGLATAS_FUNCTIONAL_SELECTED_GATES;
    try {
      const evidence = validateFci04Owner(this.discovered, this.records, gate);
      if (result.status !== 'passed') {
        throw new Error(`${FCI04_JOURNEY}: overall runner status is ${result.status}.`);
      }
      mkdirSync('test-results', { recursive: true });
      writeFileSync(`test-results/fci04-${gate}-owner.json`, `${JSON.stringify({
        ...evidence,
        candidateSha: process.env.TARGET_SHA ?? process.env.GITHUB_SHA ?? null,
      }, null, 2)}\n`);
      console.log(`${FCI04_JOURNEY}: required ${gate} owner and all steps passed.`);
    } catch (error) {
      console.error(error instanceof Error ? error.message : String(error));
      return { status: 'failed' };
    }
    return undefined;
  }
}
