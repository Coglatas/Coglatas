/* eslint-disable complexity, func-style, max-statements, no-magic-numbers, one-var, require-unicode-regexp, sort-imports */
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { SourceInventory } from './check-av-mig-source.mjs';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');

async function readJson(relativePath) {
  return JSON.parse(await readFile(resolve(repoRoot, relativePath), 'utf8'));
}

const pinnedSourceSha = '4e6a10903a5a472ca89f833aba993a29e1b2cf73';
const inventoryPath = 'docs/migration/avalonia/angular-frontend-inventory.json';
const legacyFreezePath = 'docs/migration/avalonia/angular-feature-freeze-matrix.json';
const targetMapPath = 'docs/migration/avalonia/angular-target-surface-map-v5.8.1.json';
const persistedStatePath = 'docs/migration/avalonia/angular-persisted-state-inventory.json';

const novemberCoreRoutes = new Set([
  '/login',
  '/session-expired',
  '/permission-denied',
  '/',
  '/workspaces/:workspaceId/projects',
  '/workspaces',
  '/projects/:projectId/tasks/new',
  '/projects/:projectId/tasks/:taskId',
  '/projects/:projectId',
  '/projects',
]);

const postNovemberRoutes = new Set([
  '/messages',
  '/conversations/:conversationId',
  '/workspaces/:workspaceId/channels/:conversationId',
  '/dm/:conversationId',
  '/admin/audit/findings',
  '/admin/audit/claims-evidence',
  '/admin/audit/package-export',
  '/admin/audit',
  '/admin/invites',
  '/admin/export-diagnostics',
]);

function byPath(entries) {
  return new Map(entries.map((entry) => [entry.path, entry]));
}

test('source inventory, legacy freeze snapshot and v5.8.1 target map cover the same pinned 38-route source', async () => {
  const [inventory, legacyFreeze, targetMap] = await Promise.all([
    readJson(inventoryPath),
    readJson(legacyFreezePath),
    readJson(targetMapPath),
  ]);

  assert.equal(inventory.routes.length, 38, 'source inventory route count must remain the pinned 38-route snapshot');
  assert.equal(legacyFreeze.routes.length, 38, 'legacy freeze snapshot must still cover every pinned route');
  assert.equal(targetMap.routes.length, 38, 'v5.8.1 target map must classify every pinned route');

  const declaredPaths = SourceInventory.unique(
    SourceInventory.routesFrom(
      SourceInventory.parse(resolve(repoRoot, legacyFreeze.routeDefinition)),
    ),
    'pinned production routes',
  );
  const inventoryPaths = inventory.routes.map((route) => route.path).sort();
  const legacyPaths = legacyFreeze.routes.map((route) => route.path).sort();
  const targetPaths = targetMap.routes.map((route) => route.path).sort();
  assert.deepEqual(inventoryPaths, declaredPaths, 'source inventory must match the production route declaration');
  assert.deepEqual(legacyPaths, inventoryPaths, 'legacy source/freeze route sets must match exactly');
  assert.deepEqual(targetPaths, inventoryPaths, 'v5.8.1 target map must match the pinned source route set exactly');
  assert.equal(new Set(targetPaths).size, 38, 'pinned target route set must not contain duplicates');
  assert.equal(inventory.source.commit, pinnedSourceSha, 'source inventory must remain pinned to the approved snapshot');
  assert.equal(inventory.source.parentIssue, 764, 'inventory parent program must remain #764');
  assert.equal(inventory.source.executionRoadmapIssue, 798, 'inventory execution roadmap must remain #798');
  assert.equal(legacyFreeze.sourceSnapshot, pinnedSourceSha, 'legacy freeze must remain pinned to the approved snapshot');
  assert.equal(targetMap.sourceSnapshot, pinnedSourceSha, 'v5.8.1 target map must remain pinned to the approved snapshot');
  assert.equal(targetMap.parentProgram, 764, 'target map parent program must remain #764');
  assert.equal(targetMap.executionRoadmap, 798, 'target map execution roadmap must remain #798');
});

test('every v5.8.1 target route has PNL/mode, disposition and execution owner', async () => {
  const targetMap = await readJson(targetMapPath);
  for (const route of targetMap.routes) {
    assert.ok(typeof route.targetPnlMode === 'string' && route.targetPnlMode.trim(), `${route.path} missing targetPnlMode`);
    assert.ok(typeof route.disposition === 'string' && route.disposition.trim(), `${route.path} missing disposition`);
    assert.ok(Number.isInteger(route.owner) && route.owner > 0, `${route.path} missing Avalonia execution owner`);
    assert.ok(Array.isArray(route.support), `${route.path} support must be an array`);
  }
});

test('November scope remains aligned with #798 core roadmap', async () => {
  const legacyFreeze = await readJson(legacyFreezePath);
  const freezeByPath = byPath(legacyFreeze.routes);

  for (const path of novemberCoreRoutes) {
    const route = freezeByPath.get(path);
    assert.ok(route, `${path} must exist`);
    assert.equal(route.november, true, `${path} must remain November-required`);
    assert.equal(route.freeze, 'November Required', `${path} must use November Required freeze class`);
  }

  for (const path of postNovemberRoutes) {
    const route = freezeByPath.get(path);
    assert.ok(route, `${path} must exist`);
    assert.equal(route.november, false, `${path} must remain outside the November core preview`);
  }
});

test('v5.8.1 corrections bind invite, artifact/report and communication owners', async () => {
  const targetMap = await readJson(targetMapPath);
  const routes = byPath(targetMap.routes);

  assert.equal(routes.get('/register/invite')?.owner, 780);
  assert.match(routes.get('/register/invite')?.targetPnlMode ?? '', /PNL-00/);

  const artifactRoutes = [
    '/artifacts/:artifactId',
    '/app/projects/:projectId/tasks/:taskId/reports/:artifactVersionId',
    '/app/projects/:projectId/reports/:artifactVersionId',
    '/projects/:projectId/tasks/:taskId/reports/:artifactVersionId',
    '/projects/:projectId/reports/:artifactVersionId',
  ];
  for (const path of artifactRoutes) {
    assert.equal(routes.get(path)?.owner, 817, `${path} must be owned by #817`);
    assert.match(routes.get(path)?.targetPnlMode ?? '', /PNL-47/);
  }

  assert.equal(routes.get('/messages')?.disposition, 'IntentionallyChanged');
  assert.match(routes.get('/messages')?.targetPnlMode ?? '', /Team Chat/);
  assert.match(routes.get('/messages')?.targetPnlMode ?? '', /DM Inbox/);
  assert.match(routes.get('/conversations/:conversationId')?.targetPnlMode ?? '', /Team Chat/);
  assert.match(routes.get('/dm/:conversationId')?.targetPnlMode ?? '', /Direct Message/);
  assert.equal(routes.get('/messages/settings')?.owner, 791);
  assert.match(routes.get('/messages/settings')?.targetPnlMode ?? '', /PNL-35/);

  for (const route of targetMap.routes) {
    assert.doesNotMatch(
      route.targetPnlMode,
      /\bConversation\b/u,
      `${route.path} must not use generic Conversation as a target product surface`,
    );
  }
});

test('embedded Project WorkSurface surfaces are explicit migration units', async () => {
  const targetMap = await readJson(targetMapPath);
  const embedded = new Map(targetMap.embeddedSurfaces.map((surface) => [surface.id, surface]));

  const kanban = embedded.get('project-kanban');
  assert.equal(kanban?.owner, 782);
  assert.equal(kanban?.november, false);
  assert.equal(kanban?.freeze, 'Maintenance Only');
  assert.ok(kanban?.support.includes(814));

  const gantt = embedded.get('project-gantt');
  assert.equal(gantt?.owner, 787);
  assert.equal(gantt?.november, false);
  assert.equal(gantt?.freeze, 'Maintenance Only');
  assert.ok(gantt?.support.includes(777));
  assert.ok(gantt?.support.includes(782));

  const table = embedded.get('project-table');
  assert.equal(table?.owner, 782);
  assert.equal(table?.november, true);
  assert.equal(table?.freeze, 'November Required');
  assert.ok(table?.support.includes(776));
});

test('embedded Project surfaces carry explicit freeze classifications', async () => {
  const legacyFreeze = await readJson(legacyFreezePath);
  const embedded = new Map(legacyFreeze.embeddedSurfaces.map((surface) => [surface.surface, surface]));

  const table = embedded.get('Project Task Table/List');
  assert.equal(table?.november, true);
  assert.equal(table?.freeze, 'November Required');
  assert.equal(table?.avaloniaIssue, 782);

  const kanban = embedded.get('Project Kanban');
  assert.equal(kanban?.november, false);
  assert.equal(kanban?.freeze, 'Maintenance Only');
  assert.equal(kanban?.avaloniaIssue, 782);

  const gantt = embedded.get('Project Gantt/Schedule');
  assert.equal(gantt?.november, false);
  assert.equal(gantt?.freeze, 'Maintenance Only');
  assert.equal(gantt?.avaloniaIssue, 787);
});

test('Graph/Dock are owned Avalonia-first capabilities and Calendar remains promotion-gated', async () => {
  const targetMap = await readJson(targetMapPath);
  const embedded = new Map(targetMap.embeddedSurfaces.map((surface) => [surface.id, surface]));
  const graph = embedded.get('relation-graph');
  const dock = embedded.get('dock-layout');
  const calendar = embedded.get('calendar');

  assert.equal(graph?.owner, 816);
  assert.equal(graph?.disposition, 'AvaloniaFirst');
  assert.equal(graph?.freeze, 'Avalonia First');
  assert.equal(dock?.owner, 815);
  assert.equal(dock?.disposition, 'AvaloniaFirst');
  assert.equal(dock?.freeze, 'Avalonia First');
  assert.equal(calendar?.disposition, 'Deferred');
  assert.equal(calendar?.owner, null);
  assert.equal(calendar?.freeze, 'Avalonia First');
  assert.deepEqual(calendar?.semanticOwners, [782, 784]);
  assert.match(calendar?.promotionRule ?? '', /dedicated implementation Issue/);
});

test('architecture-only deferred PNLs stay explicit and out of active production routes', async () => {
  const targetMap = await readJson(targetMapPath);
  assert.deepEqual(
    targetMap.deferredPnls,
    ['PNL-21', 'PNL-22', 'PNL-23', 'PNL-24', 'PNL-36', 'PNL-37', 'PNL-45-deep-RTC'],
  );

  const deferredPrefixes = ['PNL-21', 'PNL-22', 'PNL-23', 'PNL-24', 'PNL-36', 'PNL-37', 'PNL-45'];
  const promoted = targetMap.routes.filter((route) =>
    deferredPrefixes.some((pnl) => route.targetPnlMode.includes(pnl)),
  );
  assert.deepEqual(promoted, [], 'deferred architecture-only PNLs must not be silently promoted by a production route');
});

test('platform support remains provisional and delegated to #767', async () => {
  const [inventory, legacyFreeze, targetMap] = await Promise.all([
    readJson(inventoryPath),
    readJson(legacyFreezePath),
    readJson(targetMapPath),
  ]);

  assert.equal(inventory.platformPolicy?.authorityIssue, 767);
  assert.equal(legacyFreeze.platformPolicy?.authorityIssue, 767);
  assert.equal(targetMap.platformPolicy?.sourceOfTruthIssue, 767);
  assert.equal(targetMap.platformPolicy?.status, 'provisional-pending-AV-MIG-03-evidence');
  assert.equal(targetMap.platformPolicy?.desktop?.role, 'reference-target');
  assert.equal(targetMap.platformPolicy?.desktop?.tier, 'pending-#767');
  assert.equal(targetMap.platformPolicy?.desktop?.releaseBlocking, 'not-claimed-by-#765');
  for (const platform of ['browserWasm', 'android', 'ios']) {
    assert.equal(targetMap.platformPolicy?.[platform]?.tier, 'pending-#767');
    assert.equal(targetMap.platformPolicy?.[platform]?.unsupportedOrSkippedIsPass, false);
  }

  const graph = legacyFreeze.nonRouteSurfaces.find((surface) =>
    surface.surface.startsWith('Graph surface requested by #798'),
  );
  assert.equal(graph?.avaloniaIssue, 816);
});

test('browser/session persisted state is fully classified and never claims authorization authority', async () => {
  const persistedState = await readJson(persistedStatePath);
  const persisted = new Map(persistedState.entries.map((entry) => [entry.id, entry]));
  const required = [
    'theme',
    'locale',
    'last-workspace',
    'my-work-projection',
    'my-work-saved-filters',
    'message-global-settings',
    'messaging-drafts',
    'messaging-navigation',
    'right-panel-mode',
    'audit-saved-views',
    'continue-working',
  ];

  for (const id of required) {
    const entry = persisted.get(id);
    assert.ok(entry, `persisted-state family ${id} must be inventoried`);
    assert.ok(typeof entry.disposition === 'string' && entry.disposition.length > 0, `${id} missing disposition`);
    assert.ok(Number.isInteger(entry.targetOwner) && entry.targetOwner > 0, `${id} missing target owner`);
  }

  assert.equal(persistedState.assertions.classifiedFamilies, required.length);
  assert.equal(persistedState.assertions.authTokenOrCookieStorage, 'none identified in localStorage/sessionStorage inventory');
  assert.equal(persistedState.assertions.teamChatDmDraftTargetSeparation, true);
  assert.equal(persistedState.assertions.rendererLocalNavigationIsNotSemanticState, true);

  const draft = persisted.get('messaging-drafts');
  assert.equal(draft?.storage, 'sessionStorage');
  assert.equal(draft?.targetOwner, 785);
  assert.match(draft?.security ?? '', /separate Team Chat and DM draft owners/);

  const nav = persisted.get('messaging-navigation');
  assert.equal(nav?.disposition, 'RetireAndRebuild');
  assert.match(nav?.targetFamily ?? '', /renderer-local/);
});

test('every routed Angular retirement remains gated by #797', async () => {
  const legacyFreeze = await readJson(legacyFreezePath);
  for (const route of legacyFreeze.routes) {
    assert.match(
      route.deleteCondition ?? '',
      /#797/u,
      `${route.path}: Angular delete condition must remain gated by #797`,
    );
  }
});

test('active target inventory has no owner-TBD and does not invent NgRx migration work', async () => {
  const [inventory, targetMap] = await Promise.all([readJson(inventoryPath), readJson(targetMapPath)]);
  assert.equal(targetMap.assertions.routeCount, 38);
  assert.equal(targetMap.assertions.artifactReportOwner, 817);
  assert.equal(targetMap.assertions.graphOwner, 816);
  assert.equal(targetMap.assertions.inviteRegistrationOwner, 780);
  assert.equal(targetMap.assertions.mixedConversationTargetForbidden, true);

  const angularDependencies = new Map(inventory.dependencies.map((dependency) => [dependency.name, dependency]));
  assert.equal(angularDependencies.get('NgRx packages')?.purpose, 'declared dependencies; no production imports found');

  const graphSourceGap = inventory.unresolvedSurfaces.find((surface) => surface.name === 'graph');
  assert.equal(graphSourceGap?.historicalSnapshot, true);
  assert.equal(graphSourceGap?.status, 'historical-source-gap-resolved');
  assert.equal(graphSourceGap?.targetOwnerIssue, 816);

  const activeUnknownOwners = targetMap.routes.filter((route) => !Number.isInteger(route.owner));
  assert.deepEqual(activeUnknownOwners, []);
});
