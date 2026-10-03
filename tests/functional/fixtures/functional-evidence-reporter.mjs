import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { validateFci04Owner } from './fci04-owner-reporter.mjs';

const owners = {
  core: ['FUNC-TASK-001'],
  files: ['FUNC-FILE-002'],
  collaboration: ['FUNC-MSG-001', 'FUNC-NOTIF-001'],
  'authz-negative': ['FUNC-AUTHZ-001', 'FUNC-AUTHZ-002'],
};

export function requiredOwners(domain, gate) {
  if (!owners[domain] || !['functional-fast', 'functional-full', 'functional-extended'].includes(gate)) {
    throw new Error('Invalid Functional domain or gate.');
  }
  return [...owners[domain], ...(domain === 'collaboration' && gate !== 'functional-fast' ? ['FUNC-ANN-001'] : [])];
}

export function ownerResult(test) {
  const attempts = test.results ?? [];
  let status = 'BLOCKED';
  if (test.expectedStatus !== 'passed' || attempts.some((attempt) => attempt.status === 'skipped')) {
    status = 'SKIPPED';
  } else if (attempts.length === 1 && attempts[0].status === 'passed' && attempts[0].retry === 0) {
    status = 'PASS';
  } else if (attempts.some((attempt) => attempt.status === 'passed')) {
    status = 'FLAKY';
  } else if (attempts.some((attempt) => ['failed', 'timedOut'].includes(attempt.status))) {
    status = 'FAIL';
  }
  return { status, attempts: attempts.length, durationMs: attempts.reduce((sum, attempt) => sum + attempt.duration, 0) };
}

function completedOwnerResult(owner, journeyId, gate) {
  const outcome = ownerResult(owner);
  if (journeyId === 'FUNC-TASK-001' && outcome.status === 'PASS') {
    const [attempt] = owner.results;
    try {
      // Extended reuses the complete eleven-step owner rather than granting
      // a new meaning to the existing fast/full completion validator.
      validateFci04Owner([{
        journey: journeyId,
        backend: 'real',
        gates: owner.annotations.find((annotation) => annotation.type === 'functional-gates')?.description,
      }], [{
        status: attempt.status,
        retry: attempt.retry,
        steps: attempt.steps.filter((step) => step.category === 'test.step' && !step.error).map((step) => step.title),
      }], gate === 'functional-extended' ? 'functional-full' : gate);
    } catch {
      outcome.status = 'BLOCKED';
    }
  }
  return outcome;
}

/** Persist only allowlisted metadata; no titles, assertion bodies, headers, or attachments. */
export default class FunctionalEvidenceReporter {
  onBegin(_config, suite) {
    this.startedAt = new Date();
    this.tests = suite.allTests();
  }

  onEnd(result) {
    const gate = process.env.COGLATAS_FUNCTIONAL_SELECTED_GATES;
    const domain = process.env.COGLATAS_FUNCTIONAL_DOMAIN;
    const required = requiredOwners(domain, gate);
    const quarantine = JSON.parse(readFileSync('tests/functional/quarantine.json', 'utf8')).entries;
    const journeys = required.map((journeyId) => {
      const matches = this.tests.filter((test) => test.annotations.some((annotation) =>
        annotation.type === 'journey' && annotation.description === journeyId));
      const real = matches.length === 1 && matches[0].annotations.some((annotation) =>
        annotation.type === 'backend' && annotation.description === 'real');
      const outcome = real ? completedOwnerResult(matches[0], journeyId, gate) : { status: 'BLOCKED', attempts: 0, durationMs: 0 };
      if (quarantine.some((entry) => entry.journeyId === journeyId)) {
        outcome.status = 'QUARANTINED';
      }
      return { journeyId, ...outcome };
    });
    const completedAt = new Date();
    const setup = JSON.parse(readFileSync(`artifacts/functional/setup-${domain}.json`, 'utf8'));
    const evidence = {
      schemaVersion: 1,
      commitSha: process.env.TARGET_SHA,
      gate,
      runId: process.env.GITHUB_RUN_ID,
      runAttempt: process.env.GITHUB_RUN_ATTEMPT,
      suite: domain,
      startedAt: this.startedAt.toISOString(),
      completedAt: completedAt.toISOString(),
      setupSeconds: setup.setupSeconds,
      testSeconds: (completedAt - this.startedAt) / 1000,
      journeys,
    };
    mkdirSync('artifacts/functional', { recursive: true });
    writeFileSync(`artifacts/functional/lane-${domain}.json`, `${JSON.stringify(evidence, null, 2)}\n`);
    if (result.status !== 'passed' || this.tests.length !== required.length || journeys.some((journey) => journey.status !== 'PASS')) {
      console.error('Required Functional owner evidence is missing or not first-attempt PASS.');
      return { status: 'failed' };
    }
    return undefined;
  }
}
