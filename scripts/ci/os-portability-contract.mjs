import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import * as yaml from 'js-yaml';

const DEFAULT_CONTRACT = 'scripts/ci/os-portability.contract.json';
const TRUST_REGISTRY = 'governance/workflow-trust-policy.json';
const REQUIRED_MATRIX = ['ubuntu-latest', 'windows-latest', 'macos-latest'];
const BOUNDED_RUNS_ON = "${{ (matrix.os == 'windows-latest' && 'windows-latest') || (matrix.os == 'macos-latest' && 'macos-latest') || 'ubuntu-latest' }}";
const REQUIRED_ASSUMPTIONS = [
  'shell',
  'gnu-tools',
  'temporary-paths',
  'path-separators',
  'executable-bits',
  'case-sensitivity',
  'line-endings'
];
const REQUIRED_ACTIONS = ['actions/checkout', 'actions/setup-dotnet', 'actions/setup-node', 'actions/upload-artifact'];

export async function loadOsPortabilityContract(contractPath = DEFAULT_CONTRACT) {
  let contract;
  try {
    contract = JSON.parse(await readFile(contractPath, 'utf8'));
  } catch (error) {
    throw new Error(`Unable to read OS portability contract ${contractPath}: ${errorMessage(error)}`);
  }
  return validateOsPortabilityContract(contract);
}

export function validateOsPortabilityContract(contract) {
  assertPlainObject(contract, 'contract');
  if (contract.schemaVersion !== 1) {
    throw new Error(`OS portability schemaVersion must be 1; received ${String(contract.schemaVersion)}.`);
  }
  if (contract.name !== 'os-portability') {
    throw new Error('OS portability contract name must be os-portability.');
  }
  if (contract.issue !== 590) {
    throw new Error('OS portability contract must remain bound to Issue #590.');
  }
  readRepositoryPath(contract.workflow, 'workflow');
  assertExactUniqueStrings(contract.matrix, REQUIRED_MATRIX, 'matrix');

  assertPlainObject(contract.toolchain, 'toolchain');
  readRepositoryPath(contract.toolchain.dotnetGlobalJson, 'toolchain.dotnetGlobalJson');
  if (!Number.isInteger(contract.toolchain.nodeMajor) || contract.toolchain.nodeMajor < 1) {
    throw new Error('toolchain.nodeMajor must be a positive integer.');
  }
  if (!/^\d+\.\d+\.\d+$/u.test(contract.toolchain.npmVersion)) {
    throw new Error('toolchain.npmVersion must be an exact semantic version.');
  }
  readUniqueRepositoryPaths(contract.toolchain.packageManagerManifests, 'toolchain.packageManagerManifests');
  assertExactUniqueStrings(contract.dependencyRoots, ['.', 'frontend'], 'dependencyRoots');

  assertPlainObject(contract.dotnet, 'dotnet');
  readRepositoryPath(contract.dotnet.solution, 'dotnet.solution');
  readRepositoryPath(contract.dotnet.testProject, 'dotnet.testProject');
  if (contract.dotnet.configuration !== 'Release') {
    throw new Error('dotnet.configuration must be Release.');
  }
  if (contract.dotnet.testFilter !== 'Portability=CrossPlatform') {
    throw new Error('dotnet.testFilter must be Portability=CrossPlatform.');
  }
  if (!Number.isInteger(contract.dotnet.minimumTests) || contract.dotnet.minimumTests < 1) {
    throw new Error('dotnet.minimumTests must be a positive integer.');
  }
  assertPlainObject(contract.dotnet.trait, 'dotnet.trait');
  if (contract.dotnet.trait.name !== 'Portability' || contract.dotnet.trait.value !== 'CrossPlatform') {
    throw new Error('dotnet.trait must declare Portability=CrossPlatform.');
  }
  if (!Array.isArray(contract.dotnet.testClasses) || contract.dotnet.testClasses.length < 1) {
    throw new Error('dotnet.testClasses must contain at least one class.');
  }
  const classNames = new Set();
  const classPaths = new Set();
  for (const [index, entry] of contract.dotnet.testClasses.entries()) {
    assertPlainObject(entry, `dotnet.testClasses[${index}]`);
    const classPath = readRepositoryPath(entry.path, `dotnet.testClasses[${index}].path`);
    const className = readNonEmptyString(entry.className, `dotnet.testClasses[${index}].className`);
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/u.test(className)) {
      throw new Error(`Invalid portable test class name: ${className}.`);
    }
    if (classNames.has(className) || classPaths.has(classPath)) {
      throw new Error(`Duplicate portable test class entry: ${className} (${classPath}).`);
    }
    classNames.add(className);
    classPaths.add(classPath);
  }

  assertPlainObject(contract.frontend, 'frontend');
  if (contract.frontend.workspace !== 'frontend') {
    throw new Error('frontend.workspace must identify the active frontend directory.');
  }
  for (const field of ['buildScript', 'testScript']) {
    readNonEmptyString(contract.frontend[field], `frontend.${field}`);
  }
  readUniqueStrings(contract.frontend.helperScripts, 'frontend.helperScripts');

  assertPlainObject(contract.compatCritical, 'compatCritical');
  readRepositoryPath(contract.compatCritical.contract, 'compatCritical.contract');
  if (contract.compatCritical.profile !== 'os-portability') {
    throw new Error('compatCritical.profile must reuse the COMPAT-04 os-portability profile.');
  }
  if (contract.compatCritical.discoveryProject !== 'chromium-desktop') {
    throw new Error('compatCritical.discoveryProject must remain discovery-only on chromium-desktop.');
  }
  if (contract.compatCritical.executeBrowserInMatrix !== false) {
    throw new Error('OS portability must not execute browsers in the three-OS matrix.');
  }

  assertPlainObject(contract.boundaries, 'boundaries');
  for (const boundaryName of ['databaseIntegration', 'realBrowser', 'docker']) {
    const boundary = contract.boundaries[boundaryName];
    assertPlainObject(boundary, `boundaries.${boundaryName}`);
    if (boundary.matrixMode !== 'excluded') {
      throw new Error(`boundaries.${boundaryName}.matrixMode must be excluded.`);
    }
    readNonEmptyString(boundary.reason, `boundaries.${boundaryName}.reason`);
  }
  if (contract.boundaries.databaseIntegration.ownerWorkflow !== '.github/workflows/ci.yml') {
    throw new Error('Database integration must remain owned by .github/workflows/ci.yml.');
  }
  if (contract.boundaries.docker.ownerWorkflow !== '.github/workflows/ci.yml') {
    throw new Error('Docker validation must remain owned by .github/workflows/ci.yml.');
  }
  if (contract.boundaries.realBrowser.ownerIssue !== 587) {
    throw new Error('Real browser compatibility must remain owned by Issue #587.');
  }

  if (!Array.isArray(contract.assumptions)) {
    throw new Error('assumptions must be an array.');
  }
  const assumptionIds = [];
  for (const [index, assumption] of contract.assumptions.entries()) {
    assertPlainObject(assumption, `assumptions[${index}]`);
    assumptionIds.push(readNonEmptyString(assumption.id, `assumptions[${index}].id`));
    if (assumption.matrixHandling !== 'portable') {
      throw new Error(`assumptions[${index}].matrixHandling must be portable.`);
    }
    readNonEmptyString(assumption.repositoryHandling, `assumptions[${index}].repositoryHandling`);
    readNonEmptyString(assumption.enforcement, `assumptions[${index}].enforcement`);
  }
  assertExactUniqueStrings(assumptionIds, REQUIRED_ASSUMPTIONS, 'assumptions ids');

  return contract;
}

export async function verifyRepositoryOsPortability(repositoryRoot = process.cwd(), options = {}) {
  const root = path.resolve(repositoryRoot);
  const contractPath = path.resolve(root, options.contractPath ?? DEFAULT_CONTRACT);
  const contract = await loadOsPortabilityContract(contractPath);
  const workflowText = await readUtf8(root, contract.workflow);
  const allowlist = JSON.parse(await readUtf8(root, 'governance/github-actions-allowlist.json'));
  const trustRegistry = JSON.parse(await readUtf8(root, TRUST_REGISTRY));

  validateWorkflowText(contract, workflowText, allowlist);
  validateRunnerRoutingRegistry(contract, trustRegistry);
  const declaredToolchain = await validateToolchainDeclarations(root, contract);
  await validatePortableTestSources(root, contract);
  await validateCompatibilityProfile(root, contract);
  await validateLinuxOwnedBoundaries(root, contract);

  const trackedPaths = listTrackedPaths(root);
  const collisions = findCaseInsensitiveCollisions(trackedPaths);
  if (collisions.length > 0) {
    throw new Error(`Tracked paths collide on a case-insensitive filesystem: ${collisions.map((entry) => entry.join(' / ')).join('; ')}`);
  }

  if (options.runtime === true) {
    validateObservedToolchain(contract, declaredToolchain.dotnetSdk);
  }

  return {
    matrix: [...contract.matrix],
    portableTestClasses: contract.dotnet.testClasses.length,
    minimumDotnetTests: contract.dotnet.minimumTests,
    trackedPaths: trackedPaths.length,
    runtimeValidated: options.runtime === true
  };
}

export function validateWorkflowText(contract, workflowText, allowlist) {
  const workflow = parseWorkflowDocument(workflowText, contract.workflow);
  assertPlainObject(workflow.jobs, `${contract.workflow}.jobs`);
  const portability = workflow.jobs.portability;
  assertPlainObject(portability, `${contract.workflow}.jobs.portability`);
  const failures = [];

  if (Object.keys(workflow.jobs).length !== 1) {
    failures.push('OS portability workflow must contain exactly one job named portability.');
  }
  if (workflow.permissions?.contents !== 'read') {
    failures.push('Top-level permissions.contents must be read.');
  }
  for (const forbiddenRootKey of ['continue-on-error', 'container', 'defaults', 'services', 'shell']) {
    if (hasOwn(workflow, forbiddenRootKey)) {
      failures.push(`Workflow root must not declare ${forbiddenRootKey}.`);
    }
  }

  const strategy = portability.strategy;
  assertPlainObject(strategy, 'jobs.portability.strategy');
  const matrix = strategy.matrix;
  assertPlainObject(matrix, 'jobs.portability.strategy.matrix');
  if (!sameStringArray(matrix.os, contract.matrix)) {
    failures.push(`workflow matrix must be exactly: ${contract.matrix.join(', ')}.`);
  }
  if (strategy['fail-fast'] !== false) {
    failures.push('matrix fail-fast must be false.');
  }
  if (strategy['max-parallel'] !== 3) {
    failures.push('matrix max-parallel must be 3.');
  }
  if (portability['runs-on'] !== BOUNDED_RUNS_ON) {
    failures.push('bounded matrix runner routing is missing or changed.');
  }
  if (portability.name !== 'OS portability (${{ matrix.os }})') {
    failures.push('OS-specific job identity is missing or changed.');
  }
  if (!Number.isInteger(portability['timeout-minutes']) || portability['timeout-minutes'] < 1 || portability['timeout-minutes'] > 60) {
    failures.push('bounded timeout must be an integer from 1 through 60 minutes.');
  }
  for (const forbiddenJobKey of ['continue-on-error', 'container', 'defaults', 'services', 'shell']) {
    if (hasOwn(portability, forbiddenJobKey)) {
      failures.push(`jobs.portability must not declare ${forbiddenJobKey}.`);
    }
  }

  if (!Array.isArray(portability.steps)) {
    throw new Error('jobs.portability.steps must be an array.');
  }
  const stepsById = new Map();
  for (const [index, step] of portability.steps.entries()) {
    assertPlainObject(step, `jobs.portability.steps[${index}]`);
    const id = readNonEmptyString(step.id, `jobs.portability.steps[${index}].id`);
    if (stepsById.has(id)) {
      failures.push(`Duplicate portability step id: ${id}.`);
    }
    stepsById.set(id, step);
    if (hasOwn(step, 'continue-on-error')) {
      failures.push(`Portability step ${id} must not declare continue-on-error.`);
    }
    if (hasOwn(step, 'shell')) {
      failures.push(`Portability step ${id} must not override shell.`);
    }
  }

  const requiredStepIds = [
    'checkout',
    'setup_dotnet',
    'setup_node',
    'npm_toolchain',
    'contract',
    'root_dependencies',
    'frontend_dependencies',
    'dotnet_restore',
    'dotnet_build',
    'dotnet_tests',
    'dotnet_results',
    'frontend_build',
    'frontend_tests',
    'frontend_helpers',
    'compat_critical',
    'evidence',
    'upload_evidence'
  ];
  for (const id of requiredStepIds) {
    if (!stepsById.has(id)) {
      failures.push(`Missing required portability step id: ${id}.`);
    }
  }

  const actions = allowlist?.actions;
  if (!actions || typeof actions !== 'object') {
    failures.push('GitHub Action allowlist is missing actions.');
  } else {
    const actionSteps = new Map([
      ['actions/checkout', 'checkout'],
      ['actions/setup-dotnet', 'setup_dotnet'],
      ['actions/setup-node', 'setup_node'],
      ['actions/upload-artifact', 'upload_evidence']
    ]);
    for (const action of REQUIRED_ACTIONS) {
      const entry = actions[action];
      const stepId = actionSteps.get(action);
      const step = stepId ? stepsById.get(stepId) : null;
      if (!entry || typeof entry !== 'object') {
        failures.push(`Required action is not allowlisted: ${action}.`);
      } else if (!step || step.uses !== `${action}@${entry.sha}`) {
        failures.push(`Portability step ${stepId ?? '<unknown>'} must use immutable ${action}@${entry.sha}.`);
      }
    }
  }

  const requiredCommands = new Map([
    ['npm_toolchain', [`npm install --global npm@${contract.toolchain.npmVersion} --no-audit --no-fund`]],
    ['contract', ['node scripts/ci/os-portability-contract.mjs --runtime']],
    ['root_dependencies', ['npm ci --ignore-scripts --no-audit --no-fund']],
    ['frontend_dependencies', ['npm --prefix frontend ci --ignore-scripts --no-audit --no-fund']],
    ['dotnet_restore', [`dotnet restore ${contract.dotnet.solution}`]],
    ['dotnet_build', [`dotnet build ${contract.dotnet.solution} --configuration Release --no-restore`]],
    ['dotnet_tests', [
      `dotnet test ${contract.dotnet.testProject} --configuration Release --no-build`,
      `--filter "${contract.dotnet.testFilter}"`
    ]],
    ['dotnet_results', ['node scripts/ci/verify-os-portability-results.mjs']],
    ['frontend_build', ['npm --prefix frontend run build']],
    ['frontend_tests', ['npm --prefix frontend test']],
    ['frontend_helpers', contract.frontend.helperScripts.map((script) => `npm --prefix frontend run ${script}`)],
    ['compat_critical', [
      `node scripts/ci/verify-compat-critical.mjs --profile ${contract.compatCritical.profile} --project ${contract.compatCritical.discoveryProject}`
    ]],
    ['evidence', ['node scripts/ci/write-os-portability-evidence.mjs']]
  ]);
  for (const [stepId, expectedCommands] of requiredCommands.entries()) {
    const step = stepsById.get(stepId);
    const run = typeof step?.run === 'string' ? step.run : '';
    for (const expected of expectedCommands) {
      if (!run.includes(expected)) {
        failures.push(`Portability step ${stepId} is missing required command: ${expected}.`);
      }
    }
  }

  const checkout = stepsById.get('checkout');
  if (checkout?.with?.['persist-credentials'] !== false) {
    failures.push('Checkout must set persist-credentials: false.');
  }
  const setupDotnet = stepsById.get('setup_dotnet');
  if (setupDotnet?.with?.['global-json-file'] !== contract.toolchain.dotnetGlobalJson) {
    failures.push(`setup-dotnet must use ${contract.toolchain.dotnetGlobalJson}.`);
  }
  const setupNode = stepsById.get('setup_node');
  if (String(setupNode?.with?.['node-version'] ?? '') !== String(contract.toolchain.nodeMajor)) {
    failures.push(`setup-node must use Node ${contract.toolchain.nodeMajor}.`);
  }
  const evidence = stepsById.get('evidence');
  if (evidence?.if !== 'always()') {
    failures.push('Evidence creation must run with if: always().');
  }
  const uploadEvidence = stepsById.get('upload_evidence');
  if (uploadEvidence?.if !== 'always()') {
    failures.push('Evidence upload must run with if: always().');
  }
  if (uploadEvidence?.with?.name !== 'os-portability-${{ matrix.os }}-${{ github.run_attempt }}') {
    failures.push('Evidence artifact name must remain OS- and attempt-specific.');
  }
  if (uploadEvidence?.with?.['if-no-files-found'] !== 'error') {
    failures.push('Evidence upload must fail when no files are found.');
  }

  const forbiddenRunPatterns = [
    [/\bdocker(?:-compose|\s+compose|\s+run|\s+build)\b/iu, 'Docker execution'],
    [/\bplaywright\s+(?:install|test)\b/iu, 'browser execution'],
    [/(?:^|[\s"'])\/tmp(?:\/|[\s"']|$)/mu, 'hard-coded /tmp path'],
    [/\b(?:sed|grep|awk|chmod)\s+/iu, 'GNU or executable-bit command']
  ];
  for (const [stepId, step] of stepsById.entries()) {
    const run = typeof step.run === 'string' ? step.run : '';
    for (const [pattern, label] of forbiddenRunPatterns) {
      if (pattern.test(run)) {
        failures.push(`Portability step ${stepId} must not contain ${label}.`);
      }
    }
  }

  if (failures.length > 0) {
    throw new Error(`OS portability workflow validation failed:\n- ${failures.join('\n- ')}`);
  }
}

export function validateRunnerRoutingRegistry(contract, registry) {
  const routing = registry?.runner_routing;
  if (!routing || typeof routing !== 'object' || Array.isArray(routing)) {
    throw new Error(`${TRUST_REGISTRY} must declare runner_routing.`);
  }
  const labels = Reflect.get(routing, 'github_hosted_labels');
  if (!Array.isArray(labels) || contract.matrix.some((label) => !labels.includes(label))) {
    throw new Error(`${TRUST_REGISTRY} must classify every portability matrix label as GitHub-hosted.`);
  }
  const expressions = Reflect.get(routing, 'approved_dynamic_expressions');
  if (!Array.isArray(expressions) || !expressions.includes(BOUNDED_RUNS_ON)) {
    throw new Error(`${TRUST_REGISTRY} must approve the bounded portability runner expression.`);
  }
}

export function findCaseInsensitiveCollisions(paths) {
  const byFoldedPath = new Map();
  for (const entry of paths) {
    const normalized = String(entry).replaceAll('\\', '/').normalize('NFC');
    const folded = normalized.toLowerCase();
    const values = byFoldedPath.get(folded) ?? new Set();
    values.add(normalized);
    byFoldedPath.set(folded, values);
  }
  return [...byFoldedPath.values()]
    .filter((values) => values.size > 1)
    .map((values) => [...values].sort())
    .sort((left, right) => left[0].localeCompare(right[0]));
}

export function npmVersionCommand(platform = process.platform) {
  if (platform === 'win32') {
    return { command: 'cmd.exe', args: ['/d', '/s', '/c', 'npm --version'] };
  }
  return { command: 'npm', args: ['--version'] };
}

async function validateToolchainDeclarations(root, contract) {
  const globalJson = JSON.parse(await readUtf8(root, contract.toolchain.dotnetGlobalJson));
  const sdkVersion = globalJson?.sdk?.version;
  if (typeof sdkVersion !== 'string' || !/^\d+\.\d+\.\d+$/u.test(sdkVersion)) {
    throw new Error(`${contract.toolchain.dotnetGlobalJson} must declare one exact sdk.version.`);
  }
  if (globalJson.sdk.rollForward !== 'disable' || globalJson.sdk.allowPrerelease !== false) {
    throw new Error(`${contract.toolchain.dotnetGlobalJson} must disable roll-forward and prerelease SDK selection.`);
  }

  const expectedPackageManager = `npm@${contract.toolchain.npmVersion}`;
  for (const manifestPath of contract.toolchain.packageManagerManifests) {
    const manifest = JSON.parse(await readUtf8(root, manifestPath));
    if (manifest.packageManager !== expectedPackageManager) {
      throw new Error(`${manifestPath} packageManager is ${String(manifest.packageManager)}; expected ${expectedPackageManager}.`);
    }
  }
  return { dotnetSdk: sdkVersion };
}

async function validatePortableTestSources(root, contract) {
  const trait = `[Trait("${contract.dotnet.trait.name}", "${contract.dotnet.trait.value}")]`;
  for (const entry of contract.dotnet.testClasses) {
    const source = await readUtf8(root, entry.path);
    const classDeclaration = `public sealed class ${entry.className}`;
    const classIndex = source.indexOf(classDeclaration);
    if (classIndex < 0) {
      throw new Error(`Portable test class declaration is missing: ${entry.className} in ${entry.path}.`);
    }
    const attributeWindow = source.slice(Math.max(0, classIndex - 300), classIndex);
    if (!attributeWindow.includes(trait)) {
      throw new Error(`Portable test class ${entry.className} must carry ${trait}.`);
    }
  }
}

async function validateCompatibilityProfile(root, contract) {
  const compat = JSON.parse(await readUtf8(root, contract.compatCritical.contract));
  const profile = compat?.profiles?.[contract.compatCritical.profile];
  if (!profile || typeof profile !== 'object') {
    throw new Error(`COMPAT-04 profile is missing: ${contract.compatCritical.profile}.`);
  }
  const selected = Array.isArray(compat.tests)
    ? compat.tests.filter((entry) => entry?.status === 'active' && entry?.profiles?.includes(contract.compatCritical.profile))
    : [];
  if (selected.length < 1) {
    throw new Error(`COMPAT-04 profile ${contract.compatCritical.profile} selects zero active tests.`);
  }
}

async function validateLinuxOwnedBoundaries(root, contract) {
  const ownerWorkflowPath = contract.boundaries.databaseIntegration.ownerWorkflow;
  const workflow = parseWorkflowDocument(await readUtf8(root, ownerWorkflowPath), ownerWorkflowPath);
  assertPlainObject(workflow.jobs, `${ownerWorkflowPath}.jobs`);
  const postgresOwners = Object.entries(workflow.jobs).filter(([, candidate]) => {
    if (!candidate || typeof candidate !== 'object' || Array.isArray(candidate)) {
      return false;
    }
    if (candidate['runs-on'] !== 'ubuntu-latest') {
      return false;
    }
    const services = candidate.services;
    if (!services || typeof services !== 'object' || Array.isArray(services)) {
      return false;
    }
    const postgres = services.postgres;
    return Boolean(
      postgres
      && typeof postgres === 'object'
      && !Array.isArray(postgres)
      && typeof postgres.image === 'string'
      && postgres.image.startsWith('postgres:')
    );
  });
  if (postgresOwners.length < 1) {
    throw new Error('Canonical CI must retain one Ubuntu job that directly owns a PostgreSQL service for DB integration.');
  }
}

function validateObservedToolchain(contract, expectedDotnetSdk) {
  const failures = [];
  const expectedNode = String(contract.toolchain.nodeMajor);
  const actualNode = process.versions.node.split('.')[0];
  if (actualNode !== expectedNode) {
    failures.push(`Node major is ${actualNode}; expected ${expectedNode}.`);
  }

  const npmCommand = npmVersionCommand();
  const npm = runVersion(npmCommand.command, npmCommand.args);
  if (!npm.ok || npm.value !== contract.toolchain.npmVersion) {
    failures.push(`npm version is ${npm.value ?? '<unavailable>'}; expected ${contract.toolchain.npmVersion}.`);
  }

  const dotnet = runVersion('dotnet', ['--version']);
  if (!dotnet.ok || dotnet.value !== expectedDotnetSdk) {
    failures.push(`.NET SDK is ${dotnet.value ?? '<unavailable>'}; expected ${expectedDotnetSdk} from global.json.`);
  }

  if (!['linux', 'win32', 'darwin'].includes(process.platform)) {
    failures.push(`Unsupported runtime platform: ${process.platform}.`);
  }

  if (failures.length > 0) {
    throw new Error(`Observed OS portability toolchain failed:\n- ${failures.join('\n- ')}`);
  }
}

function runVersion(command, args) {
  const result = spawnSync(command, args, { encoding: 'utf8', windowsHide: true });
  if (result.status !== 0) {
    return { ok: false, value: null };
  }
  return { ok: true, value: result.stdout.trim() };
}

function listTrackedPaths(root) {
  const result = spawnSync('git', ['ls-files', '-z', '--cached'], {
    cwd: root,
    encoding: 'utf8',
    windowsHide: true
  });
  if (result.status !== 0) {
    throw new Error(`Unable to inventory tracked paths: ${result.stderr?.trim() || 'git ls-files failed'}`);
  }
  return result.stdout.split('\0').filter(Boolean);
}

function parseWorkflowDocument(source, label) {
  let document;
  try {
    document = yaml.load(source);
  } catch (error) {
    throw new Error(`Unable to parse workflow YAML ${label}: ${errorMessage(error)}`);
  }
  assertPlainObject(document, label);
  return document;
}

function sameStringArray(actual, expected) {
  return Array.isArray(actual)
    && actual.length === expected.length
    && actual.every((entry, index) => entry === expected[index]);
}

function hasOwn(value, key) {
  return Boolean(value) && Object.hasOwn(value, key);
}

async function readUtf8(root, relativePath) {
  try {
    return await readFile(path.resolve(root, relativePath), 'utf8');
  } catch (error) {
    throw new Error(`Unable to read ${relativePath}: ${errorMessage(error)}`);
  }
}

function assertExactUniqueStrings(value, expected, label) {
  const actual = readUniqueStrings(value, label);
  if (actual.length !== expected.length || actual.some((entry, index) => entry !== expected[index])) {
    throw new Error(`${label} must be exactly: ${expected.join(', ')}.`);
  }
}

function readUniqueRepositoryPaths(value, label) {
  const paths = readUniqueStrings(value, label);
  return paths.map((entry, index) => readRepositoryPath(entry, `${label}[${index}]`));
}

function readUniqueStrings(value, label) {
  if (!Array.isArray(value) || value.length < 1) {
    throw new Error(`${label} must be a non-empty array.`);
  }
  const normalized = value.map((entry, index) => readNonEmptyString(entry, `${label}[${index}]`));
  if (new Set(normalized).size !== normalized.length) {
    throw new Error(`${label} must not contain duplicates.`);
  }
  return normalized;
}

function readRepositoryPath(value, label) {
  const result = readNonEmptyString(value, label);
  if (path.isAbsolute(result) || result.includes('..') || result.includes('\\')) {
    throw new Error(`${label} must be a normalized repository-relative path: ${result}.`);
  }
  return result;
}

function readNonEmptyString(value, label) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`${label} must be a non-empty string.`);
  }
  return value.trim();
}

function assertPlainObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} must be an object.`);
  }
}

function errorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

function parseArguments(args) {
  const options = { contractPath: DEFAULT_CONTRACT, runtime: false };
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (argument === '--runtime') {
      options.runtime = true;
    } else if (argument === '--static') {
      options.runtime = false;
    } else if (argument === '--contract') {
      const value = args[++index];
      if (!value) {
        throw new Error('--contract requires a path.');
      }
      options.contractPath = value;
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }
  return options;
}

function isMainModule() {
  const entryPoint = process.argv[1];
  return Boolean(entryPoint) && import.meta.url === pathToFileURL(path.resolve(entryPoint)).href;
}

if (isMainModule()) {
  try {
    const options = parseArguments(process.argv.slice(2));
    const result = await verifyRepositoryOsPortability(process.cwd(), options);
    console.log(
      `OS portability contract passed: matrix=${result.matrix.join(',')}; portableClasses=${result.portableTestClasses}; minimumTests=${result.minimumDotnetTests}; trackedPaths=${result.trackedPaths}; runtime=${result.runtimeValidated}.`
    );
  } catch (error) {
    console.error(errorMessage(error));
    process.exitCode = 1;
  }
}
