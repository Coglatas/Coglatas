import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

export async function readRequiredTestTitles(manifestPath) {
  const manifest = await readFile(manifestPath, 'utf8');
  const testTitles = manifest
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .filter((line) => line.length > 0 && !line.startsWith('#'));

  if (testTitles.length === 0) {
    throw new Error(`Required-test manifest contains no active test titles: ${manifestPath}`);
  }

  const duplicates = testTitles.filter((title, index) => testTitles.indexOf(title) !== index);
  if (duplicates.length > 0) {
    throw new Error(`Required-test manifest contains duplicate titles: ${[...new Set(duplicates)].join(', ')}`);
  }

  return testTitles;
}

export async function buildPlaywrightGrep(manifestPath, options = {}) {
  const testTitles = await readRequiredTestTitles(manifestPath);

  if (options.verifyPath) {
    const specSource = await readFile(options.verifyPath, 'utf8');
    const missingTitles = testTitles.filter(
      (title) => !specSource.includes(`test('${title}'`) && !specSource.includes(`test("${title}"`)
    );

    if (missingTitles.length > 0) {
      throw new Error(
        `Required real-backend tests are missing or renamed in ${options.verifyPath}:\n${missingTitles
          .map((title) => `  - ${title}`)
          .join('\n')}`
      );
    }
  }

  const escapedTitles = testTitles.map((title) => title.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&'));

  // Playwright applies --grep to a fully-qualified title that includes project,
  // file, and test.describe names before the test title. Match an exact required
  // test title at the end of that string rather than anchoring the beginning.
  return `(?:^|\\s)(?:${escapedTitles.join('|')})$`;
}

function parseArguments(args) {
  const remaining = [...args];
  const manifestPath = remaining.shift();
  if (!manifestPath) {
    throw new Error('Usage: node scripts/ci/build-playwright-grep.mjs <manifest> [--verify <spec-file>]');
  }

  let verifyPath = '';
  while (remaining.length > 0) {
    const argument = remaining.shift();
    if (argument !== '--verify') {
      throw new Error(`Unknown argument: ${argument}`);
    }

    verifyPath = remaining.shift() ?? '';
    if (!verifyPath) {
      throw new Error('--verify requires a spec file path.');
    }
  }

  return { manifestPath, verifyPath };
}

function isMainModule() {
  const entryPoint = process.argv[1];
  return Boolean(entryPoint) && import.meta.url === pathToFileURL(entryPoint).href;
}

if (isMainModule()) {
  try {
    const { manifestPath, verifyPath } = parseArguments(process.argv.slice(2));
    const grepPattern = await buildPlaywrightGrep(manifestPath, { verifyPath });
    process.stdout.write(grepPattern);
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  }
}
