import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const SYNCFUSION_IMPORT_PATTERN = /@syncfusion\/ej2-[\w-]+/u;
export const SIGNALR_IMPORT_PATTERN = /@microsoft\/signalr/u;
export const AG_GRID_ENTERPRISE_PATTERN = /['"](?:@ag-grid-enterprise\/[\w./-]+|ag-grid-enterprise(?:\/[\w./-]+)?)['"]/u;
export const LEGACY_THEME_TOKEN_PATTERN = /--(?:coglatas-(?:surface|border|text)-|coglatas-color-(?:bg-subtle|text-warning|text-on-action)\b)/u;
const allowedPaths = ['/shared/ui/adapters/syncfusion/', '/shared/vendor/syncfusion/'];
const allowedSignalrPaths = ['/core/realtime/signalr-realtime.transport.ts'];

export function findDisallowedSyncfusionImports(sources) {
  return sources
    .filter(({ path, source }) => {
      const normalizedPath = path.replaceAll('\\', '/');
      return SYNCFUSION_IMPORT_PATTERN.test(source) && !allowedPaths.some((allowedPath) => normalizedPath.includes(allowedPath));
    })
    .map(({ path }) => path);
}

export function findDisallowedSignalrImports(sources) {
  return sources
    .filter(({ path, source }) => {
      const normalizedPath = path.replaceAll('\\', '/');
      return SIGNALR_IMPORT_PATTERN.test(source) && !allowedSignalrPaths.some((allowedPath) => normalizedPath.endsWith(allowedPath));
    })
    .map(({ path }) => path);
}

export function findAgGridEnterpriseImports(sources) {
  return sources.filter(({ source }) => AG_GRID_ENTERPRISE_PATTERN.test(source)).map(({ path }) => path);
}

export const findLegacyThemeTokens = (sources) =>
  sources.filter(({ source }) => LEGACY_THEME_TOKEN_PATTERN.test(source)).map(({ path }) => path);

async function files(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  return (await Promise.all(entries.map((entry) => entry.isDirectory() ? files(join(directory, entry.name)) : [join(directory, entry.name)]))).flat();
}

export async function validateSyncfusionImportBoundary(root) {
  const sourceFiles = (await files(root)).filter((file) => file.endsWith('.ts'));
  const sources = await Promise.all(sourceFiles.map(async (path) => ({ path, source: await readFile(path, 'utf8') })));
  return findDisallowedSyncfusionImports(sources);
}

const root = fileURLToPath(new URL('../src/app/', import.meta.url));
const appFiles = await files(root);
const textSources = await Promise.all(appFiles
  .filter((file) => file.endsWith('.ts') || file.endsWith('.scss') || file.endsWith('.html'))
  .map(async (path) => ({ path, source: await readFile(path, 'utf8') })));
const typescriptSources = textSources.filter(({ path }) => path.endsWith('.ts'));
const offenders = findDisallowedSyncfusionImports(typescriptSources);
const signalrOffenders = findDisallowedSignalrImports(typescriptSources);
const enterpriseOffenders = findAgGridEnterpriseImports(typescriptSources);
const legacyThemeOffenders = findLegacyThemeTokens(textSources);
if (offenders.length || signalrOffenders.length || enterpriseOffenders.length || legacyThemeOffenders.length) {
  const messages = [];
  if (offenders.length) {messages.push(`Direct Syncfusion imports outside the Coglatas adapter boundary:\n${offenders.join('\n')}`);}
  if (signalrOffenders.length) {messages.push(`SignalR imports outside the Coglatas realtime transport boundary:\n${signalrOffenders.join('\n')}`);}
  if (enterpriseOffenders.length) {messages.push(`AG Grid Enterprise imports are not approved:\n${enterpriseOffenders.join('\n')}`);}
  if (legacyThemeOffenders.length) {messages.push(`Legacy or undefined theme tokens must use the canonical --coglatas-color-* contract:\n${legacyThemeOffenders.join('\n')}`);}
  throw new Error(messages.join('\n'));
}
