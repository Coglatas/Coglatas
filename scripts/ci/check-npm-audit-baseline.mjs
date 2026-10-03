import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const [reportDir, baselinePath] = process.argv.slice(2);
if (!reportDir || !baselinePath) {
  console.error('Usage: node check-npm-audit-baseline.mjs <report-dir> <baseline.json>');
  process.exit(2);
}

const baselineDocument = JSON.parse(await readFile(baselinePath, 'utf8'));
if (baselineDocument.version !== 1 || !Array.isArray(baselineDocument.findings)) {
  throw new Error('npm audit baseline must have version=1 and a findings array.');
}

const reportFiles = (await readdir(reportDir))
  .filter(file => file.endsWith('.json'))
  .sort();
if (reportFiles.length === 0) {
  throw new Error(`No npm audit JSON reports found in ${reportDir}.`);
}

const actual = [];
for (const file of reportFiles) {
  const report = JSON.parse(await readFile(path.join(reportDir, file), 'utf8'));
  const reportKey = path.basename(file, '.json');
  for (const [packageName, entry] of Object.entries(report.vulnerabilities ?? {})) {
    if (!['high', 'critical'].includes(entry.severity)) {
      continue;
    }
    actual.push(normalizeFinding({
      report: reportKey,
      package: packageName,
      severity: entry.severity,
      via: normalizeVia(entry.via ?? [])
    }));
  }
}

const baseline = baselineDocument.findings.map(normalizeFinding);
const actualByKey = new Map(actual.map(item => [findingKey(item), item]));
const baselineByKey = new Map(baseline.map(item => [findingKey(item), item]));

const unreviewed = [...actualByKey.entries()]
  .filter(([key]) => !baselineByKey.has(key))
  .map(([, item]) => item);
const stale = [...baselineByKey.entries()]
  .filter(([key]) => !actualByKey.has(key))
  .map(([, item]) => item);

console.log(`npm audit High/Critical findings: actual=${actual.length}, baseline=${baseline.length}`);

for (const finding of unreviewed) {
  console.error(`::error::Unreviewed npm audit finding: ${formatFinding(finding)}`);
}
for (const finding of stale) {
  console.warn(`::warning::Reviewed npm audit baseline entry is not currently reported: ${formatFinding(finding)}`);
}

if (unreviewed.length > 0) {
  console.error('Update dependencies first, then update the reviewed baseline only for intentionally accepted residual risk.');
  process.exit(1);
}

if (stale.length > 0) {
  console.log(
    `npm audit baseline retains ${stale.length} reviewed finding(s) not currently reported; ` +
    'this does not suppress any current High/Critical finding.'
  );
}

console.log('All currently reported npm audit High/Critical findings are covered by the reviewed baseline.');

function normalizeFinding(finding) {
  if (!finding || typeof finding !== 'object') {
    throw new Error('Invalid npm audit baseline finding.');
  }
  const report = requiredString(finding.report, 'report');
  const packageName = requiredString(finding.package, 'package');
  const severity = requiredString(finding.severity, 'severity');
  if (!['high', 'critical'].includes(severity)) {
    throw new Error(`Baseline severity must be high or critical: ${severity}`);
  }
  const via = Array.isArray(finding.via)
    ? [...new Set(finding.via.map(value => requiredString(value, 'via')))].sort()
    : [];
  return { report, package: packageName, severity, via };
}

function normalizeVia(via) {
  return [...new Set(via.map(item => {
    if (typeof item === 'string') {
      return `dependency:${item}`;
    }
    if (item && typeof item === 'object') {
      if (item.source !== undefined && item.source !== null) {
        return `advisory:${String(item.source)}`;
      }
      if (typeof item.url === 'string' && item.url.length > 0) {
        return `url:${item.url}`;
      }
      if (typeof item.title === 'string' && item.title.length > 0) {
        return `title:${item.title}`;
      }
    }
    return 'unknown';
  }))].sort();
}

function findingKey(finding) {
  return JSON.stringify([finding.report, finding.package, finding.severity, finding.via]);
}

function formatFinding(finding) {
  return `${finding.report}:${finding.package}:${finding.severity}:via=${finding.via.join(',')}`;
}

function requiredString(value, label) {
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`npm audit baseline ${label} must be a non-empty string.`);
  }
  return value;
}
