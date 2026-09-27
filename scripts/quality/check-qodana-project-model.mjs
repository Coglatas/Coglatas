#!/usr/bin/env node
import { existsSync, readFileSync, writeFileSync } from 'node:fs';

const changedFilesPath = process.env.QODANA_CHANGED_FILES_PATH,
  configuredThresholdParser = (name, defaultValue) => {
    const rawValue = process.env[name] || defaultValue,
      value = Number(rawValue);

    if (!/^\d+$/u.test(rawValue) || !Number.isFinite(value) || !Number.isSafeInteger(value)) {
      throw new TypeError(
        `${name} must be a finite, non-negative integer; received ${JSON.stringify(rawValue)}.`
      );
    }

    return value;
  },
  criticalThreshold = configuredThresholdParser('QODANA_CRITICAL_THRESHOLD', '0'),
  ruleBaselinePath =
    process.env.QODANA_RULE_BASELINE_PATH || 'scripts/quality/qodana-rule-baseline.json',
  sarifPath =
    process.argv[2] ||
    process.env.QODANA_SARIF_PATH ||
    (process.env.RUNNER_TEMP ? `${process.env.RUNNER_TEMP}/qodana/results/qodana.sarif.json` : undefined),
  unresolvedFileThreshold = configuredThresholdParser('QODANA_UNRESOLVED_FILE_THRESHOLD', '40'),
  unresolvedThreshold = configuredThresholdParser('QODANA_UNRESOLVED_THRESHOLD', '200');

if (!sarifPath) {
  console.error('Qodana SARIF path was not supplied.');
  process.exit(1);
}

if (!existsSync(sarifPath)) {
  console.error(`Qodana SARIF file was not found: ${sarifPath}`);
  process.exit(1);
}

if (changedFilesPath && !existsSync(changedFilesPath)) {
  throw new Error(`Qodana changed-files list was not found: ${changedFilesPath}`);
}

if (!existsSync(ruleBaselinePath)) {
  throw new Error(`Qodana rule baseline was not found: ${ruleBaselinePath}`);
}

let sarif;
let ruleBaseline;
try {
  sarif = JSON.parse(readFileSync(sarifPath, 'utf8'));
} catch (error) {
  console.error(`Qodana SARIF file is not valid JSON: ${sarifPath}`);
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}

try {
  ruleBaseline = JSON.parse(readFileSync(ruleBaselinePath, 'utf8'));
  validateRuleBaseline(ruleBaseline);
} catch (error) {
  console.error(`Qodana rule baseline is invalid: ${ruleBaselinePath}`);
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}

const changedFiles = changedFilesPath
    ? new Set(readFileSync(changedFilesPath, 'utf8').split('\0').filter(Boolean))
    : null,
  results = (sarif.runs || []).flatMap((run) => run.results || []),
  ruleIndex = new Map();

for (const run of sarif.runs || []) {
  for (const rule of run.tool?.driver?.rules || []) {
    if (rule.id) {
      ruleIndex.set(rule.id, rule);
    }
  }
}

const severityCounts = new Map();
const unresolvedResults = [];
const modelFailureResults = [];

const unresolvedPattern =
  /(cannot resolve symbol|cannot find module|unresolved reference|the type or namespace name .* could not be found|CS0246|CS0103|CS0234|TS2307)/i;
const modelFailurePattern =
  /(NETSDK1045|requested SDK.*not found|MSBuild project load|project model|bootstrap failed|restore failed|build failed|failed to load project|failed to load solution|NU1101|NU1102|NU1301|NU1605)/i;

for (const result of results) {
  const rule = result.ruleId ? ruleIndex.get(result.ruleId) : undefined;
  const text = [
    result.ruleId,
    result.level,
    result.message?.text,
    result.message?.markdown,
    result.properties?.problem?.severity,
    result.properties?.qodanaSeverity,
    result.properties?.severity,
    rule?.name,
    rule?.shortDescription?.text,
    rule?.fullDescription?.text
  ]
    .filter(Boolean)
    .join('\n');

  const severity = normalizeSeverity(result, rule);
  severityCounts.set(severity, (severityCounts.get(severity) || 0) + 1);

  if (unresolvedPattern.test(text)) {
    unresolvedResults.push(result);
  }

  if (modelFailurePattern.test(text)) {
    modelFailureResults.push(result);
  }
}

const ruleCounts = countBy(results.map((result) => result.ruleId || '<missing-rule-id>')),
  ruleRegressions = findRuleRegressions(ruleCounts, ruleBaseline.ruleBudgets),
  categoryCounts = countBy(unresolvedResults.map(classifyUnresolved)),
  changedResults = changedFiles
    ? results.filter((result) =>
        (result.locations || []).some((location) =>
          changedFiles.has(location.physicalLocation?.artifactLocation?.uri)
        )
      )
    : [],
  unresolvedDependencies = countBy(unresolvedResults.map(firstUnresolvedDependency)),
  unresolvedFiles = new Set(unresolvedResults.flatMap(resultFiles));

const summary = {
  sarifPath,
  totalFindings: results.length,
  severityCounts: Object.fromEntries([...severityCounts.entries()].sort()),
  changedFiles: changedFiles?.size ?? Number(),
  changedFindings: changedResults.length,
  ruleBaselinePath,
  ruleBaselineSourceRevision: ruleBaseline.sourceRevision ?? null,
  ruleBaselineTotalFindings: ruleBaseline.totalFindings,
  currentRuleCounts: Object.fromEntries([...ruleCounts.entries()].sort(([left], [right]) => left.localeCompare(right))),
  ruleRegressions,
  criticalFindings: ['critical', 'error'].reduce(
    (count, severity) => count + (severityCounts.get(severity) || Number()),
    Number()
  ),
  criticalThreshold,
  unresolvedSymbols: unresolvedResults.length,
  unresolvedAffectedFiles: unresolvedFiles.size,
  unresolvedThreshold,
  unresolvedFileThreshold,
  unresolvedDependencies: topEntries(unresolvedDependencies, 20),
  unresolvedCategories: Object.fromEntries([...categoryCounts.entries()].sort()),
  modelFailures: modelFailureResults.length
};

console.log(JSON.stringify(summary, null, 2));

if (process.env.QODANA_PROJECT_MODEL_SUMMARY_PATH) {
  writeFileSync(process.env.QODANA_PROJECT_MODEL_SUMMARY_PATH, `${JSON.stringify(summary, null, 2)}\n`);
}

if (modelFailureResults.length > 0) {
  console.error('Qodana reported project-model, restore, build, SDK, or package-resolution failures.');
  process.exit(1);
}

if (summary.criticalFindings > criticalThreshold) {
  throw new Error(
    `Qodana critical findings exceed the configured threshold: ${summary.criticalFindings} > ${criticalThreshold}.`
  );
}

if (unresolvedResults.length > unresolvedThreshold || unresolvedFiles.size > unresolvedFileThreshold) {
  console.error(
    `Qodana unresolved-symbol findings exceed the project-model collapse guard: ` +
      `${unresolvedResults.length} findings across ${unresolvedFiles.size} files.`
  );
  process.exit(1);
}

if (ruleRegressions.length > 0) {
  throw new Error(
    `Qodana rule debt increased above the ratchet baseline: ${JSON.stringify(ruleRegressions)}`
  );
}

function validateRuleBaseline(baseline) {
  if (baseline?.version !== 1 || !baseline.ruleBudgets || typeof baseline.ruleBudgets !== 'object') {
    throw new TypeError('Expected Qodana rule baseline version 1 with a ruleBudgets object.');
  }

  for (const [ruleId, budget] of Object.entries(baseline.ruleBudgets)) {
    if (!ruleId || !Number.isSafeInteger(budget) || budget < 0) {
      throw new TypeError(`Invalid Qodana rule baseline entry: ${JSON.stringify({ ruleId, budget })}`);
    }
  }

  if (!Number.isSafeInteger(baseline.totalFindings) || baseline.totalFindings < 0) {
    throw new TypeError('Qodana rule baseline totalFindings must be a non-negative integer.');
  }
}

function findRuleRegressions(currentCounts, budgets) {
  const regressions = [];

  for (const [ruleId, current] of currentCounts) {
    const budget = budgets[ruleId] ?? 0;
    if (current > budget) {
      regressions.push({ ruleId, budget, current, delta: current - budget });
    }
  }

  return regressions.sort(
    (left, right) => right.delta - left.delta || left.ruleId.localeCompare(right.ruleId)
  );
}

function normalizeSeverity(result, rule) {
  const raw =
    result.properties?.problem?.severity ||
    result.properties?.qodanaSeverity ||
    result.properties?.severity ||
    rule?.properties?.problem?.severity ||
    rule?.properties?.qodanaSeverity ||
    rule?.defaultConfiguration?.level ||
    result.level ||
    'none';

  return String(raw).toLowerCase();
}

function resultFiles(result) {
  return (result.locations || [])
    .map((location) => location.physicalLocation?.artifactLocation?.uri)
    .filter(Boolean);
}

function firstUnresolvedDependency(result) {
  const text = `${result.message?.text || ''}\n${result.message?.markdown || ''}`;
  const patterns = [
    /cannot resolve symbol ['"`]?([^'"`\s.,;:)]+)/i,
    /cannot find module ['"`]([^'"`]+)['"`]/i,
    /unresolved reference[:\s]+['"`]?([^'"`\s.,;:)]+)/i,
    /the type or namespace name ['"`]?([^'"`\s.,;:)]+)['"`]?.*could not be found/i,
    /(CS0246|CS0103|CS0234|TS2307)/i
  ];

  for (const pattern of patterns) {
    const match = text.match(pattern);
    if (match) {
      return match[2] || match[1];
    }
  }

  return 'unknown';
}

function classifyUnresolved(result) {
  const dependency = firstUnresolvedDependency(result);
  const files = resultFiles(result).join('\n');
  const combined = `${dependency}\n${files}`;

  if (/@angular|rxjs|typescript|Component|Injectable|NgModule|Router|Observable|HttpClient/i.test(combined)) {
    return 'frontend';
  }

  if (/Microsoft|AspNetCore|EntityFrameworkCore|ControllerBase|DbContext|IdentityUser|IActionResult/i.test(combined)) {
    return 'backend-platform';
  }

  if (/Coglatas|Application|Infrastructure|Domain/i.test(combined)) {
    return 'internal-project';
  }

  if (/Generated|\.g\.cs|\.Designer\.cs|openapi|swagger|client/i.test(combined)) {
    return 'generated';
  }

  return 'other';
}

function countBy(values) {
  const counts = new Map();
  for (const value of values) {
    counts.set(value, (counts.get(value) || 0) + 1);
  }
  return counts;
}

function topEntries(counts, limit) {
  return Object.fromEntries([...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, limit));
}
