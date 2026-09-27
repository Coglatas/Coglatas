#!/usr/bin/env node
import { existsSync, readFileSync, writeFileSync } from 'node:fs';

const [sarifPath, outputPath = 'scripts/quality/qodana-rule-baseline.json'] = process.argv.slice(2);

if (!sarifPath) {
  console.error('Usage: node scripts/quality/write-qodana-rule-baseline.mjs <qodana.sarif.json> [output.json]');
  process.exit(1);
}

const sarif = JSON.parse(readFileSync(sarifPath, 'utf8')),
  results = (sarif.runs || []).flatMap((run) => run.results || []),
  ruleCounts = new Map();

for (const result of results) {
  const ruleId = result.ruleId || '<missing-rule-id>';
  ruleCounts.set(ruleId, (ruleCounts.get(ruleId) || 0) + 1);
}

const ruleBudgets = Object.fromEntries([...ruleCounts.entries()].sort(([left], [right]) => left.localeCompare(right))),
  sourceRevision = sarif.runs?.[0]?.versionControlProvenance?.[0]?.revisionId || null,
  candidate = {
    version: 1,
    sourceRevision,
    totalFindings: results.length,
    ruleBudgets
  };

if (existsSync(outputPath) && process.env.QODANA_ALLOW_BASELINE_INCREASE !== '1') {
  const current = JSON.parse(readFileSync(outputPath, 'utf8')),
    increases = [];

  for (const [ruleId, count] of Object.entries(ruleBudgets)) {
    const previous = current.ruleBudgets?.[ruleId] ?? 0;
    if (count > previous) {
      increases.push({ ruleId, previous, current: count });
    }
  }

  if (increases.length > 0) {
    console.error('Refusing to raise the Qodana rule baseline without QODANA_ALLOW_BASELINE_INCREASE=1.');
    console.error(JSON.stringify(increases, null, 2));
    process.exit(1);
  }
}

writeFileSync(outputPath, `${JSON.stringify(candidate, null, 2)}\n`);
console.log(`Wrote ${results.length} Qodana findings across ${ruleCounts.size} rules to ${outputPath}.`);
