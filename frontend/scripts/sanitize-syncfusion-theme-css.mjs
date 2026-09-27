import { readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptPath = fileURLToPath(import.meta.url);
const frontendRoot = path.resolve(path.dirname(scriptPath), '..');
const defaultThemeRoot = path.join(
  frontendRoot,
  'dist',
  'coglatas-web',
  'assets',
  'vendor',
  'syncfusion'
);

const cssImportPattern = /@import\s*(?:url\(\s*(?:"[^"]*"|'[^']*'|[^)]*)\s*\)|"[^"]*"|'[^']*')\s*;/giu;
const googleFontsReferencePattern = /https:\/\/fonts\.googleapis\.com\//iu;

export function stripExternalGoogleFontImports(css) {
  return css.replace(cssImportPattern, (statement) =>
    googleFontsReferencePattern.test(statement) ? '' : statement
  );
}

async function findCssFiles(directory) {
  let entries;
  try {
    entries = await readdir(directory, { withFileTypes: true });
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    throw new Error(`Unable to inspect Syncfusion theme directory ${directory}: ${reason}`);
  }

  const files = [];
  for (const entry of entries) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...await findCssFiles(entryPath));
    } else if (entry.isFile() && entry.name.endsWith('.css')) {
      files.push(entryPath);
    }
  }

  return files.sort();
}

export async function sanitizeSyncfusionThemeCss(themeRoot = defaultThemeRoot) {
  const cssFiles = await findCssFiles(themeRoot);
  if (cssFiles.length === 0) {
    throw new Error(`No Syncfusion theme CSS files were found under ${themeRoot}.`);
  }

  let changedFiles = 0;
  for (const cssFile of cssFiles) {
    const original = await readFile(cssFile, 'utf8');
    const sanitized = stripExternalGoogleFontImports(original);

    if (googleFontsReferencePattern.test(sanitized)) {
      throw new Error(`External Google Fonts reference remains in ${cssFile}.`);
    }

    if (sanitized !== original) {
      await writeFile(cssFile, sanitized, 'utf8');
      changedFiles += 1;
    }
  }

  return { scannedFiles: cssFiles.length, changedFiles };
}


async function main() {
  const themeRoot = process.argv[2]
    ? path.resolve(process.cwd(), process.argv[2])
    : defaultThemeRoot;
  const result = await sanitizeSyncfusionThemeCss(themeRoot);
  console.log(
    `Syncfusion theme CSS sanitized: scanned=${result.scannedFiles} changed=${result.changedFiles}`
  );
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  await main();
}
