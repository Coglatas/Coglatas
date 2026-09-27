#!/usr/bin/env node

import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

const [inputPath, outputPath, requestedCategory = 'qodana-dotnet-community'] = process.argv.slice(2);

if (!inputPath || !outputPath) {
  console.error(
    'Usage: node scripts/quality/prepare-qodana-sarif-for-github.mjs <input.sarif.json> <output.sarif.json> [category]',
  );
  process.exit(2);
}

const category = requestedCategory.replace(/\/+$/, '');
if (!category) {
  throw new Error('GitHub Code Scanning category must not be empty.');
}

const sarif = JSON.parse(await readFile(inputPath, 'utf8'));
if (sarif.version !== '2.1.0' || !Array.isArray(sarif.runs)) {
  throw new Error('Expected a SARIF 2.1.0 document with a runs array.');
}

let removedExactDuplicates = 0;

for (const run of sarif.runs) {
  const automationDetails = {
    ...(run.automationDetails ?? {}),
    // A trailing slash means "stable category, no per-run id" to GitHub.
    // Qodana normally emits a report-unique automation id/guid; retaining
    // those values can make successive uploads look like unrelated analyses.
    id: `${category}/`,
  };

  delete automationDetails.guid;
  run.automationDetails = automationDetails;

  if (!Array.isArray(run.results)) {
    continue;
  }

  const seen = new Set();
  run.results = run.results.filter((result) => {
    const key = JSON.stringify(result);
    if (seen.has(key)) {
      removedExactDuplicates += 1;
      return false;
    }

    seen.add(key);
    return true;
  });
}

await mkdir(dirname(outputPath), { recursive: true });
await writeFile(outputPath, `${JSON.stringify(sarif, null, 2)}\n`, 'utf8');

console.log(
  `Prepared Qodana SARIF for GitHub Code Scanning: category=${category}, exactDuplicatesRemoved=${removedExactDuplicates}`,
);
