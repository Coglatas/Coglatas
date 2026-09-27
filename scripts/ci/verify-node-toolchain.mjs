import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';

const expectedNodeMajor = '24';
const expectedPackageManager = 'npm@11.17.0';
const failures = [];
const observations = [];

const digest = '@sha256:[a-f0-9]{64}';
await verifyDockerBase('Dockerfile', new RegExp(`^FROM\\s+node:(\\d+)(?:\\.[^@\\s]*)?${digest}\\s+AS\\s+frontend-build\\s*$`, 'm'), 'production frontend-build');
await verifyDockerBase('frontend.Dockerfile', new RegExp(`^FROM\\s+node:(\\d+)(?:\\.[^-@\\s]*)?-alpine${digest}\\s*$`, 'm'), 'frontend development container');
await verifyPinnedDockerBase('Dockerfile', new RegExp(`^FROM\\s+mcr\\.microsoft\\.com/dotnet/sdk:10\\.0\\.401${digest}\\s+AS\\s+build\\s*$`, 'm'), 'production build SDK');
await verifyPinnedDockerBase('Dockerfile', new RegExp(`^FROM\\s+mcr\\.microsoft\\.com/dotnet/aspnet:10\\.0\\.11${digest}\\s+AS\\s+runtime\\s*$`, 'm'), 'production runtime');
await verifyPinnedDockerBase('backend.Dockerfile', new RegExp(`^FROM\\s+mcr\\.microsoft\\.com/dotnet/sdk:10\\.0\\.401${digest}\\s*$`, 'm'), 'development SDK');
await verifyPackageManager('package.json');
await verifyPackageManager('frontend/package.json');
await verifyWorkflowNodeVersions('.github/workflows');

if (failures.length > 0) {
  console.error('Node toolchain policy failed:');
  for (const failure of failures) {
    console.error(`- ${failure}`);
  }
  process.exitCode = 1;
} else {
  console.log('Node toolchain policy passed.');
  for (const observation of observations) {
    console.log(`- ${observation}`);
  }
}

async function verifyDockerBase(filePath, pattern, label) {
  const content = await readFile(filePath, 'utf8');
  const match = pattern.exec(content);
  if (!match) {
    failures.push(`${filePath} does not declare the expected ${label} Node base image.`);
    return;
  }

  const major = match[1];
  observations.push(`${filePath}: Node ${major} (${label})`);
  if (major !== expectedNodeMajor) {
    failures.push(`${filePath} uses Node ${major}; repository standard is Node ${expectedNodeMajor}.`);
  }
}

async function verifyPinnedDockerBase(filePath, pattern, label) {
  const content = await readFile(filePath, 'utf8');
  if (!pattern.test(content)) {
    failures.push(`${filePath} does not declare an immutable ${label} base image digest.`);
    return;
  }

  observations.push(`${filePath}: immutable ${label} base image`);
}

async function verifyPackageManager(filePath) {
  const packageJson = JSON.parse(await readFile(filePath, 'utf8'));
  const packageManager = packageJson.packageManager;
  observations.push(`${filePath}: ${String(packageManager ?? '<missing packageManager>')}`);
  if (packageManager !== expectedPackageManager) {
    failures.push(`${filePath} packageManager is ${String(packageManager)}; expected ${expectedPackageManager}.`);
  }
}

async function verifyWorkflowNodeVersions(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  for (const entry of entries) {
    if (!entry.isFile() || !/\.ya?ml$/i.test(entry.name)) {continue;}

    const filePath = path.join(directory, entry.name);
    const content = await readFile(filePath, 'utf8');
    if (!/uses:\s*actions\/setup-node@/i.test(content)) {continue;}

    const matches = [...content.matchAll(/node-version:\s*["']?([^\s"'#]+)["']?/g)];
    if (matches.length === 0) {
      failures.push(`${filePath} uses actions/setup-node but does not declare node-version.`);
      continue;
    }

    for (const match of matches) {
      const configured = match[1];
      const major = /^(\d+)/.exec(configured)?.[1];
      observations.push(`${filePath}: setup-node ${configured}`);
      if (major !== expectedNodeMajor) {
        failures.push(`${filePath} configures setup-node ${configured}; repository standard is Node ${expectedNodeMajor}.`);
      }
    }
  }
}
