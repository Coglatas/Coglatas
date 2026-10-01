import { spawnSync } from 'node:child_process';

function sanitizeProjectName(value) {
  const normalized = value
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^[^a-z0-9]+/, '')
    .replace(/-+/g, '-')
    .slice(0, 63);

  return normalized || `coglatas-playwright-${process.pid}`;
}

function runDocker(args) {
  return spawnSync('docker', args, {
    stdio: 'inherit',
    env: process.env,
  });
}

function ensureDockerVolume(name) {
  const result = runDocker(['volume', 'create', name]);
  if (result.error || result.signal || (result.status ?? 1) !== 0) {
    console.error(`Failed to create persistent Docker cache volume: ${name}`);
    process.exit(result.status ?? 1);
  }
}

const requestedProjectName =
  process.env.COMPOSE_PROJECT_NAME ||
  [
    'coglatas-playwright',
    process.env.GITHUB_RUN_ID || 'local',
    process.env.GITHUB_RUN_ATTEMPT || '1',
    process.env.GITHUB_JOB || process.pid,
  ].join('-');

const projectName = sanitizeProjectName(requestedProjectName);
const npmCacheVolume = sanitizeProjectName(
  process.env.PLAYWRIGHT_NPM_CACHE_VOLUME || 'coglatas-playwright-npm-cache-node24-v1',
);
const angularCacheVolume = sanitizeProjectName(
  process.env.PLAYWRIGHT_ANGULAR_CACHE_VOLUME || 'coglatas-playwright-angular-cache-node24-v1',
);

process.env.PLAYWRIGHT_NPM_CACHE_VOLUME = npmCacheVolume;
process.env.PLAYWRIGHT_ANGULAR_CACHE_VOLUME = angularCacheVolume;

ensureDockerVolume(npmCacheVolume);
ensureDockerVolume(angularCacheVolume);

const composeArgs = ['compose', '-p', projectName, '-f', 'infra/compose/dev/playwright.yml'];

console.log(`Using Docker Compose project: ${projectName}`);
console.log(`Using persistent npm cache volume: ${npmCacheVolume}`);
console.log(`Using persistent Angular cache volume: ${angularCacheVolume}`);

const testResult = runDocker([
  ...composeArgs,
  'run',
  '--build',
  '--rm',
  'angular-playwright',
]);

const cleanupResult = runDocker([
  ...composeArgs,
  'down',
  '--volumes',
  '--remove-orphans',
]);

if (testResult.error) {
  console.error(testResult.error);
  process.exit(1);
}

if (testResult.signal) {
  console.error(`Docker Compose terminated by signal ${testResult.signal}.`);
  process.exit(1);
}

if ((testResult.status ?? 1) !== 0) {
  process.exit(testResult.status ?? 1);
}

if (cleanupResult.error || cleanupResult.signal || (cleanupResult.status ?? 1) !== 0) {
  console.error('Playwright completed, but Docker Compose cleanup failed.');
  process.exit(cleanupResult.status ?? 1);
}
