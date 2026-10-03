/* eslint-disable max-lines, max-lines-per-function -- FCI-04 keeps one canonical owner journey and its cleanup/evidence contract together. */
import { randomUUID } from 'node:crypto';
import { expect, type Locator, type Page, test } from '@playwright/test';

import {
  functionalFullExpansionEnabled,
  selectedFunctionalGates,
} from '../fixtures/functional-gate-selection.mjs';
import { functionalMetadata } from '../fixtures/functional-metadata.mjs';

interface CleanupState {
  originalTaskDetail: TaskDetail | null;
  originalTaskScope: TaskExecutionScope | null;
  scopeRestored: boolean;
  taskDetailsRestored: boolean;
}

interface TaskBriefField {
  value?: unknown;
}

interface TaskDetail {
  task: {
    brief?: {
      constraints?: TaskBriefField;
      deliverable?: TaskBriefField;
      goal?: TaskBriefField;
    };
    description?: unknown;
    dueDate?: unknown;
    id?: unknown;
    plannedEndDate?: unknown;
    plannedStartDate?: unknown;
    priority?: unknown;
    progressPercent?: unknown;
    startDate?: unknown;
    title?: unknown;
    uiPermissions?: { canUpdate?: unknown };
    version?: unknown;
  };
}

interface TaskExecutionResult {
  report?: {
    bodyMarkdown?: unknown;
    contentSha256?: unknown;
    title?: unknown;
  };
  runId?: unknown;
  status?: unknown;
}

interface TaskExecutionRun {
  id?: unknown;
  snapshotProjectFilesEnabled?: unknown;
  snapshotScopeOrigin?: unknown;
  snapshotWebEnabled?: unknown;
  status?: unknown;
}

interface TaskExecutionScope {
  canManage?: unknown;
  origin?: unknown;
  taskOverridePolicy?: {
    policyV2?: unknown;
    projectFilesEnabled: boolean;
    webEnabled: boolean;
  };
  taskOverrideVersion?: unknown;
}

const smokeEmail = process.env.COGLATAS_BROWSER_SMOKE_EMAIL ?? '';
const smokePassword = process.env.COGLATAS_BROWSER_SMOKE_PASSWORD ?? '';
const smokeWorkspaceTitle = 'Browser Smoke Workspace';
const smokeSecondWorkspaceTitle = 'Browser Smoke Workspace Two';
const smokeProjectTitle = 'Browser Smoke Project';
const smokeSecondProjectTitle = 'Browser Smoke PR04 Second Project';
const smokeTaskTitle = 'Browser smoke task';
const smokeTaskFileName = 'browser-smoke-task.txt';

test.describe('FCI-04 core real-backend golden journey', () => {
  test.setTimeout(180_000);

  test.beforeAll(() => {
    if (process.env.COGLATAS_REAL_BACKEND_SMOKE !== '1') {
      throw new Error('FCI-04 requires COGLATAS_REAL_BACKEND_SMOKE=1. Use the canonical Functional Compose harness.');
    }

    const baseURL = process.env.PLAYWRIGHT_BASE_URL;
    if (!baseURL || /^(?:http:\/\/)?(?:127\.0\.0\.1|localhost):4173(?:\/|$)/i.test(baseURL)) {
      throw new Error('FCI-04 requires the Compose real backend, not the static Angular server.');
    }

    if (!smokeEmail || !smokeEmail.toLowerCase().endsWith('@example.test') || !smokePassword) {
      throw new Error('FCI-04 requires synthetic real-backend Functional credentials.');
    }
  });

  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      !['chromium-desktop', 'functional-chromium'].includes(testInfo.project.name),
      'FCI-04 runs once because it mutates a shared seeded Task inside an isolated Compose project.',
    );
  });

  test(
    'FUNC-TASK-001 authenticates, reaches Workspace/Project/Task, executes once, and preserves the durable result',
    functionalMetadata({
      journeyId: 'FUNC-TASK-001',
      gates: ['functional-fast', 'functional-full', 'functional-extended'],
      domains: ['auth', 'workspace', 'task'],
      priority: 'p0',
      backend: 'real',
      polarity: 'positive',
    }),
    async ({ page }, testInfo) => {
      const fullExpansionEnabled = functionalFullExpansionEnabled();
      const evidence: Record<string, unknown> = {
        journeyId: 'FUNC-TASK-001',
        selectedGates: selectedFunctionalGates(),
        fullExpansionEnabled,
        workspaceTitle: smokeWorkspaceTitle,
        projectTitle: smokeProjectTitle,
        taskTitle: smokeTaskTitle,
        fullNavigation: null,
        taskMutation: null,
        requestBody: null,
        requestHeaders: null,
        acceptedRun: null,
        replayedRun: null,
        durableResult: null,
        reauthorizedResult: null,
        unauthorizedStatuses: null,
      };
      let workspaceId = '';
      let secondWorkspaceId = '';
      let projectId = '';
      let taskId = '';
      let idempotencyKey = '';
      let acceptedRunId = '';
      let durableContentSha256 = 'never-match';
      let durableReportBody = '';
      const cleanupState: CleanupState = {
        originalTaskDetail: null,
        originalTaskScope: null,
        scopeRestored: false,
        taskDetailsRestored: true,
      };

      try {
        await test.step('FUNC-TASK-001 / STEP-01 authenticate', async () => {
          await login(page);
        });

        await test.step('FUNC-TASK-001 / STEP-02 establish Workspace, Project, and Task authority', async () => {
          const workspaces = await expectJsonOk(page, '/api/workspaces');
          const workspace = Array.isArray(workspaces)
            ? workspaces.find((item: Record<string, unknown>) => item.name === smokeWorkspaceTitle)
            : null;
          const secondWorkspace = Array.isArray(workspaces)
            ? workspaces.find((item: Record<string, unknown>) => item.name === smokeSecondWorkspaceTitle)
            : null;
          expect(workspace, 'seeded primary Workspace').toBeTruthy();
          workspaceId = String(workspace.id);
          if (fullExpansionEnabled) {
            expect(secondWorkspace, 'seeded secondary Workspace for full context switching').toBeTruthy();
            secondWorkspaceId = String(secondWorkspace.id);
          }

          const workspaceSwitcher = page.getByTestId('workspace-switcher');
          await expect(workspaceSwitcher).toBeVisible();
          await expect(workspaceSwitcher.locator(`option[value="${workspaceId}"]`)).toHaveCount(1);
          if (await workspaceSwitcher.inputValue() !== workspaceId) {
            await workspaceSwitcher.selectOption(workspaceId);
          }
          await expect(workspaceSwitcher).toHaveValue(workspaceId);

          const projects = await expectJsonOk(
            page,
            `/api/projects?workspaceId=${encodeURIComponent(workspaceId)}&page=1&pageSize=100`,
          );
          const project = projects.items?.find((item: Record<string, unknown>) => item.title === smokeProjectTitle);
          expect(project, 'seeded Project').toBeTruthy();
          projectId = String(project.id);
          expect(project.workspaceId, 'Project remains in the selected Workspace authority scope').toBe(workspaceId);

          const tasks = await expectJsonOk(page, `/api/projects/${projectId}/tasks?page=1&pageSize=100`);
          const task = tasks.items?.find((item: Record<string, unknown>) => item.title === smokeTaskTitle);
          expect(task, 'seeded Task').toBeTruthy();
          taskId = String(task.id);
          expect(task.hasArtifact, 'the seeded Task has a real attached Project file').toBe(true);

          const taskScope = await expectJsonOk(page, `/api/tasks/${taskId}/execution-scope`) as TaskExecutionScope;
          cleanupState.originalTaskScope = taskScope;
          expect(taskScope.canManage, 'the seeded owner may configure and run the Task').toBe(true);
        });

        if (fullExpansionEnabled) {
          await test.step('FUNC-TASK-001 / STEP-03 switch Workspace and navigate Project list to Task discovery', async () => {
            evidence.fullNavigation = await runFullNavigation(
              page,
              workspaceId,
              secondWorkspaceId,
              projectId,
              taskId,
            );
          });

          await test.step('FUNC-TASK-001 / STEP-04 persist a Task update through fresh read and reload', async () => {
            const taskDetail = await expectJsonOk(page, `/api/tasks/${taskId}`) as TaskDetail;
            cleanupState.originalTaskDetail = taskDetail;
            const originalTask = taskDetail.task;
            expect(originalTask.id).toBe(taskId);
            expect(originalTask.uiPermissions?.canUpdate, 'the seeded owner may update the Task').toBe(true);

            const durableDescription = `FCI-04 durable Task update ${randomUUID()}`;
            const originalVersion = Number(originalTask.version);
            const patchResponsePromise = waitForApiResponse(page, 'PATCH', `/api/tasks/${taskId}`);
            await page.getByTestId('task-description-input').fill(durableDescription);
            cleanupState.taskDetailsRestored = false;
            await page.getByTestId('task-save-button').click();
            const patchResponse = await patchResponsePromise;
            const patchText = await patchResponse.text();
            expect(patchResponse.status(), `Task update response: ${patchText}`).toBe(200);
            const patchBody = patchResponse.request().postDataJSON() as Record<string, unknown>;
            expect(patchBody).toMatchObject({
              description: durableDescription,
              expectedVersion: originalVersion,
            });
            expect(patchResponse.request().headers()['x-csrf-token'], 'Task update uses the Angular CSRF interceptor').toBeTruthy();
            await expect(page.getByTestId('task-save-success')).toHaveText('Task saved.');

            const freshDetail = await expectJsonOk(page, `/api/tasks/${taskId}`);
            expect(freshDetail.task.description).toBe(durableDescription);
            expect(Number(freshDetail.task.version)).toBeGreaterThan(originalVersion);

            await page.reload();
            await expect(page.getByTestId('task-detail-page')).toBeVisible();
            await expect(page.getByTestId('task-description-input')).toHaveValue(durableDescription);
            const reloadedDetail = await expectJsonOk(page, `/api/tasks/${taskId}`);
            expect(reloadedDetail.task.description).toBe(durableDescription);
            expect(Number(reloadedDetail.task.version)).toBe(Number(freshDetail.task.version));
            evidence.taskMutation = {
              originalVersion,
              updatedVersion: Number(freshDetail.task.version),
              reloadedVersion: Number(reloadedDetail.task.version),
              csrfHeaderPresent: Boolean(patchResponse.request().headers()['x-csrf-token']),
            };

            await restoreTaskDetails(page, taskId, taskDetail);
            cleanupState.taskDetailsRestored = true;
          });
        }

        await test.step('FUNC-TASK-001 / STEP-05 reach the authorized Task detail controls', async () => {
          if (!fullExpansionEnabled) {
            await page.goto(`/app/projects/${projectId}/tasks/${taskId}`);
          }
          await expect(page).toHaveURL(new RegExp(`/app/projects/${projectId}/tasks/${taskId}$`));
          await expect(page.getByTestId('task-detail-page')).toBeVisible();
          await expect(page.getByRole('heading', { name: smokeTaskTitle })).toBeVisible();
          await expect(page.getByTestId('task-editor')).toBeVisible();
          await expect(page.getByTestId('task-execution-scope')).toBeVisible();
        });

        await test.step('FUNC-TASK-001 / STEP-06 configure the authorized Project File source', async () => {
          const scopePanel = page.getByTestId('task-execution-scope');
          const taskScopeGroup = scopePanel.getByRole('group', { name: 'Task source policy' });
          await expect(taskScopeGroup).toBeVisible();
          await taskScopeGroup.getByRole('radio', { name: /Use a complete Task override/ }).check();

          for (const kind of ['Web', 'WebSite', 'ConnectedApp']) {
            await taskScopeGroup.getByRole('combobox', { name: `Task override ${kind} policy`, exact: true }).selectOption('Exclude');
          }
          await taskScopeGroup.getByRole('combobox', { name: 'Task override ProjectFile policy', exact: true }).selectOption('Allow');

          const saveResponsePromise = waitForApiResponse(page, 'PUT', `/api/tasks/${taskId}/execution-scope-override`);
          await taskScopeGroup.getByRole('button', { name: 'Save Task source policy' }).click();
          const saveResponse = await saveResponsePromise;
          const saveText = await saveResponse.text();
          expect(saveResponse.status(), `Task scope save response: ${saveText}`).toBe(200);
          expect(saveResponse.request().postDataJSON()).toMatchObject({
            webEnabled: false,
            projectFilesEnabled: true,
            policyV2: {
              web: 'Exclude',
              projectFile: 'Allow',
              webSite: 'Exclude',
              connectedApp: 'Exclude',
            },
          });
          expect(saveResponse.request().headers()['x-csrf-token'], 'Task scope save uses the Angular CSRF interceptor').toBeTruthy();

          await expect(scopePanel.getByTestId('task-context-summary-web')).toContainText('Web: Exclude');
          await expect(scopePanel.getByTestId('task-context-summary-files')).toContainText('Project files: Allow');
        });

        await test.step('FUNC-TASK-001 / STEP-07 start one authorized Task execution', async () => {
          const scopePanel = page.getByTestId('task-execution-scope');
          const startResponsePromise = waitForApiResponse(page, 'POST', `/api/tasks/${taskId}/execution-runs`);
          const startButton = scopePanel.getByTestId('task-execution-start');
          await expect(startButton).toBeVisible();
          await expect(startButton).toBeEnabled();
          await startButton.click();
          const startResponse = await startResponsePromise;
          const startText = await startResponse.text();
          expect(startResponse.status(), `Task execution response: ${startText}`).toBe(201);

          const startRequest = startResponse.request();
          const requestBody = startRequest.postDataJSON() as Record<string, unknown>;
          const requestHeaders = startRequest.headers();
          idempotencyKey = requestHeaders['idempotency-key'] ?? '';
          expect(requestBody).toEqual({});
          expect(idempotencyKey).toMatch(/^[\x20-\x7e]{8,128}$/u);
          expect(requestHeaders['x-csrf-token'], 'Task execution uses the Angular CSRF interceptor').toBeTruthy();
          for (const forbidden of ['candidateIds', 'fileIds', 'fsPath', 'materializedSources', 'evidence', 'sources']) {
            expect(JSON.stringify(requestBody)).not.toContain(forbidden);
          }

          const acceptedRun = parseJson(startText) as TaskExecutionRun;
          acceptedRunId = String(acceptedRun.id);
          expect(acceptedRun.id).toMatch(/^[0-9a-f-]{36}$/i);
          expect(acceptedRun.status).toBe('Succeeded');
          expect(acceptedRun.snapshotScopeOrigin).toBe('TaskOverride');
          expect(acceptedRun.snapshotWebEnabled).toBe(false);
          expect(acceptedRun.snapshotProjectFilesEnabled).toBe(true);
          evidence.requestBody = requestBody;
          evidence.requestHeaders = {
            idempotencyKeyPresent: idempotencyKey.length > 0,
            csrfHeaderPresent: Boolean(requestHeaders['x-csrf-token']),
          };
          evidence.acceptedRun = acceptedRun;
        });

        await test.step('FUNC-TASK-001 / STEP-08 read the durable result and prove idempotent replay', async () => {
          expect(acceptedRunId, 'accepted execution run id').toMatch(/^[0-9a-f-]{36}$/i);
          const runId = acceptedRunId;
          const scopePanel = page.getByTestId('task-execution-scope');
          await expect(scopePanel.getByTestId('task-execution-result-status')).toHaveText('Succeeded', { timeout: 30_000 });
          const report = scopePanel.getByTestId('task-execution-report');
          await expect(report).toBeVisible();
          await expect(report).toContainText('Project Files Analysis Report');
          const reportBody = scopePanel.getByTestId('task-execution-report-body');
          await expect(reportBody).toContainText(/Authorized sources consumed: [1-9]/);
          await expect(reportBody).not.toContainText(smokeTaskFileName);
          await expect(reportBody).not.toContainText('/srv/');
          await expect(reportBody).not.toContainText('Synthetic PR03C browser smoke file.');

          const durableResultResponse = await fetchFromPage(
            page,
            `/api/tasks/${taskId}/execution-runs/${runId}/result`,
          );
          expect(durableResultResponse.status, durableResultResponse.text).toBe(200);
          const durableResult = parseJson(durableResultResponse.text) as TaskExecutionResult;
          expect(durableResult.runId).toBe(runId);
          expect(durableResult.status).toBe('Succeeded');
          expect(durableResult.report?.title).toBe('Project Files Analysis Report');
          expect(durableResult.report?.bodyMarkdown).toMatch(/Authorized sources consumed: [1-9]/);
          expect(durableResult.report?.bodyMarkdown).not.toContain(smokeTaskFileName);
          expect(durableResult.report?.contentSha256, 'persisted result content hash').toMatch(/^[0-9a-f]{64}$/i);
          durableContentSha256 = String(durableResult.report?.contentSha256);
          durableReportBody = String(durableResult.report?.bodyMarkdown);
          evidence.durableResult = durableResult;

          const replay = await requestWithCsrf(
            page,
            'POST',
            `/api/tasks/${taskId}/execution-runs`,
            {},
            { 'Idempotency-Key': idempotencyKey },
          );
          expect(replay.csrfHeaderPresent).toBe(true);
          expect(replay.status, replay.text).toBe(201);
          const replayedRun = parseJson(replay.text) as TaskExecutionRun;
          expect(replayedRun.id).toBe(runId);
          expect(replayedRun.status).toBe('Succeeded');
          const replayScope = await expectJsonOk(page, `/api/tasks/${taskId}/execution-scope`);
          expect(replayScope.latestRun.id, 'retry leaves the same logical execution as the latest persisted run').toBe(runId);
          const replayResult = await expectJsonOk(page, `/api/tasks/${taskId}/execution-result`);
          expect(replayResult.runId).toBe(runId);
          expect(replayResult.report.contentSha256).toBe(durableContentSha256);
          evidence.replayedRun = replayedRun;
        });

        await test.step('FUNC-TASK-001 / STEP-09 reload the same durable result', async () => {
          await page.reload();
          await expect(page.getByTestId('task-detail-page')).toBeVisible();
          await expect(page.getByTestId('task-execution-result-status')).toHaveText('Succeeded', { timeout: 30_000 });
          await expect(page.getByTestId('task-execution-report-body')).toContainText(/Authorized sources consumed: [1-9]/);

          const reloadedResult = await expectJsonOk(
            page,
            `/api/tasks/${taskId}/execution-runs/${acceptedRunId}/result`,
          );
          expect(reloadedResult.runId).toBe(acceptedRunId);
          expect(reloadedResult.status).toBe('Succeeded');
          expect(reloadedResult.report.contentSha256).toBe(durableContentSha256);
          expect(reloadedResult.report.bodyMarkdown).toBe(durableReportBody);

          const { originalTaskScope } = cleanupState;
          if (!originalTaskScope) {
            throw new Error('FCI-04 cannot restore a Task scope that was not captured.');
          }
          await restoreTaskScope(page, taskId, originalTaskScope);
          Object.assign(cleanupState, { scopeRestored: true });
        });

        await test.step('FUNC-TASK-001 / STEP-10 logout and deny protected result access', async () => {
          const logout = await requestWithCsrf(page, 'POST', '/api/auth/logout');
          expect(logout.status, logout.text).toBe(200);

          const runId = acceptedRunId;
          const deniedRead = await fetchFromPage(page, `/api/tasks/${taskId}/execution-runs/${runId}/result`);
          const deniedStart = await requestWithCsrf(
            page,
            'POST',
            `/api/tasks/${taskId}/execution-runs`,
            {},
            { 'Idempotency-Key': `task-execution-denied-${randomUUID()}` },
          );
          expect(deniedRead.status).toBe(401);
          expect(deniedStart.status).toBe(401);

          const deniedText = `${deniedRead.text}\n${deniedStart.text}`;
          expect(deniedText).not.toContain(smokeProjectTitle);
          expect(deniedText).not.toContain(smokeTaskTitle);
          expect(deniedText).not.toContain(smokeTaskFileName);
          expect(deniedText).not.toContain('Project Files Analysis Report');
          expect(deniedText).not.toContain(durableContentSha256);
          await page.goto(`/app/projects/${projectId}/tasks/${taskId}`);
          await expect(page.getByTestId('login-page')).toBeVisible();
          await expect(page.getByTestId('task-execution-report')).toHaveCount(0);
          evidence.unauthorizedStatuses = { read: deniedRead.status, start: deniedStart.status };
        });

        await test.step('FUNC-TASK-001 / STEP-11 login again and reauthorize the result', async () => {
          await login(page);
          const reauthorizedRead = await fetchFromPage(
            page,
            `/api/tasks/${taskId}/execution-runs/${acceptedRunId}/result`,
          );
          expect(reauthorizedRead.status, reauthorizedRead.text).toBe(200);
          const reauthorizedResult = parseJson(reauthorizedRead.text) as Record<string, unknown>;
          expect(reauthorizedResult.runId).toBe(acceptedRunId);
          expect(reauthorizedResult.status).toBe('Succeeded');
          const report = reauthorizedResult.report as TaskExecutionResult['report'];
          expect(report?.contentSha256).toBe(durableContentSha256);
          expect(report?.bodyMarkdown).toBe(durableReportBody);
          await page.goto(`/app/projects/${projectId}/tasks/${taskId}`);
          await expect(page.getByTestId('task-execution-report-body')).toContainText(/Authorized sources consumed: [1-9]/);
          await page.reload();
          await expect(page.getByTestId('task-detail-page')).toBeVisible();
          const sessionResult = await expectJsonOk(page, `/api/tasks/${taskId}/execution-result`);
          expect(sessionResult.runId).toBe(acceptedRunId);
          expect(sessionResult.report.contentSha256).toBe(durableContentSha256);
          expect(sessionResult.report.bodyMarkdown).toBe(durableReportBody);
          evidence.reauthorizedResult = reauthorizedResult;
        });
      } finally {
        if ((!cleanupState.taskDetailsRestored && cleanupState.originalTaskDetail)
          || (!cleanupState.scopeRestored && cleanupState.originalTaskScope)) {
          try {
            await ensureAuthenticated(page);
            if (!cleanupState.taskDetailsRestored && cleanupState.originalTaskDetail) {
              await restoreTaskDetails(page, taskId, cleanupState.originalTaskDetail);
            }
            if (!cleanupState.scopeRestored && cleanupState.originalTaskScope) {
              await restoreTaskScope(page, taskId, cleanupState.originalTaskScope);
            }
          } catch {
            // Preserve the primary assertion. Compose discards this isolated database after the job.
          }
        }
        await testInfo.attach('fci-04-core-golden-journey-evidence.json', {
          body: JSON.stringify(evidence, null, 2),
          contentType: 'application/json',
        });
      }
    },
  );
});

async function login(page: Page): Promise<void> {
  await page.goto('/app/login');
  await expect(page.getByTestId('login-page')).toBeVisible();
  const csrf = await fetchFromPage(page, '/api/security/csrf-token');
  expect(csrf.status, csrf.text).toBe(200);

  await page.getByTestId('login-email').fill(smokeEmail);
  await page.getByTestId('login-password').fill(smokePassword);
  const loginResponsePromise = waitForApiResponse(page, 'POST', '/api/auth/login');
  await page.getByTestId('login-submit').click();
  const loginResponse = await loginResponsePromise;
  expect(loginResponse.status(), await loginResponse.text()).toBe(200);
  await expect(page).toHaveURL(/\/app\/workspaces$/);
  await expect(page.getByTestId('app-shell')).toBeVisible();
}

async function runFullNavigation(
  page: Page,
  workspaceId: string,
  secondWorkspaceId: string,
  projectId: string,
  taskId: string,
): Promise<Record<string, unknown>> {
  await page.getByRole('link', { name: 'Projects' }).first().click();
  await expect(page).toHaveURL(/\/app\/projects$/);
  await expect(page.getByTestId('projects-overview-page')).toBeVisible();
  await assertFreshProjectScope(page, workspaceId, smokeProjectTitle);
  const primaryProjectCard = page.getByTestId('project-summary-card').filter({ hasText: smokeProjectTitle }).first();
  await expect(primaryProjectCard).toBeVisible();

  const workspaceSwitcher = page.getByTestId('workspace-switcher');
  await workspaceSwitcher.selectOption(secondWorkspaceId);
  await expect(page).toHaveURL(/\/app\/workspaces$/);
  await expect(workspaceSwitcher).toHaveValue(secondWorkspaceId);
  await page.getByRole('link', { name: 'Projects' }).first().click();
  await assertFreshProjectScope(page, secondWorkspaceId, smokeSecondProjectTitle);
  await expect(workspaceSwitcher).toHaveValue(secondWorkspaceId);
  await expect(page.getByTestId('project-summary-card').filter({ hasText: smokeSecondProjectTitle }).first()).toBeVisible();
  await expect(page.getByTestId('project-summary-card').filter({ hasText: smokeProjectTitle })).toHaveCount(0);

  await page.reload();
  await expect(workspaceSwitcher).toHaveValue(secondWorkspaceId);
  await expect(page.getByTestId('project-summary-card').filter({ hasText: smokeSecondProjectTitle }).first()).toBeVisible();
  await expect(page.getByTestId('project-summary-card').filter({ hasText: smokeProjectTitle })).toHaveCount(0);

  await workspaceSwitcher.selectOption(workspaceId);
  await expect(page).toHaveURL(/\/app\/workspaces$/);
  await expect(workspaceSwitcher).toHaveValue(workspaceId);
  await page.getByRole('link', { name: 'Projects' }).first().click();
  await assertFreshProjectScope(page, workspaceId, smokeProjectTitle);
  await expect(workspaceSwitcher).toHaveValue(workspaceId);
  await expect(primaryProjectCard).toBeVisible();

  await primaryProjectCard.getByRole('link', { name: `Open ${smokeProjectTitle}` }).click();
  await expect(page).toHaveURL(new RegExp(`/app/projects/${projectId}$`));
  await expect(page.getByTestId('project-detail-page')).toBeVisible();
  await page.getByRole('tab', { name: 'List', exact: true }).click();

  const taskRow = page.locator('[role="row"]').filter({ hasText: smokeTaskTitle }).first();
  await expect(taskRow).toBeVisible();
  await clickTaskOpenDetail(page, taskRow);
  await expect(page).toHaveURL(new RegExp(`/app/projects/${projectId}/tasks/${taskId}$`));
  await expect(page.getByTestId('task-detail-page')).toBeVisible();

  const myTasksResponsePromise = waitForApiResponse(page, 'GET', '/api/me/tasks');
  await page.getByTestId('nav-my-tasks').first().click();
  const myTasksResponse = await myTasksResponsePromise;
  const myTasksText = await myTasksResponse.text();
  expect(myTasksResponse.status(), myTasksText).toBe(200);
  const myTasks = parseJson(myTasksText) as { items?: unknown[] };
  expect(myTasks.items).toEqual(expect.arrayContaining([
    expect.objectContaining({ taskId, projectId, title: smokeTaskTitle }),
  ]));
  await expect(page).toHaveURL(/\/app\/tasks$/);
  await expect(page.getByTestId('my-tasks-page')).toBeVisible();

  const taskButton = page.getByRole('button', { name: /^Browser smoke task(?:\s|$)/ }).first();
  await expect(taskButton).toBeVisible();
  await taskButton.click();
  await expect(page).toHaveURL(new RegExp(`/app/projects/${projectId}/tasks/${taskId}$`));
  await expect(page.getByTestId('task-detail-page')).toBeVisible();

  return {
    primaryWorkspaceId: workspaceId,
    secondWorkspaceId,
    projectId,
    taskId,
    projectListToDetail: true,
    taskListToDetail: true,
    myTasksDiscovery: true,
  };
}

async function assertFreshProjectScope(page: Page, workspaceId: string, expectedTitle: string): Promise<void> {
  // ProjectsFacade loads when Workspace authority changes, before navigation.
  // Re-entering its page may reuse that authorized projection; prove the UI
  // against a fresh real read without requiring a redundant navigation request.
  const projects = await expectJsonOk(
    page,
    `/api/projects?workspaceId=${encodeURIComponent(workspaceId)}&page=1&pageSize=100`,
  );
  expect(projects.items).toEqual(expect.arrayContaining([
    expect.objectContaining({ title: expectedTitle, workspaceId }),
  ]));
  for (const project of projects.items) {
    expect(project.workspaceId, 'fresh Project list retains the selected Workspace scope').toBe(workspaceId);
  }
}

async function clickTaskOpenDetail(page: Page, taskRow: Locator): Promise<void> {
  const action = taskRow.getByRole('button', { name: 'Open', exact: true });
  if (!(await action.isVisible())) {
    await page.locator('.ag-body-horizontal-scroll-viewport').evaluate((viewport) => {
      viewport.scrollLeft = viewport.scrollWidth;
    });
  }
  await expect(action).toBeVisible();
  await action.click();
}

async function restoreTaskDetails(
  page: Page,
  taskId: string,
  originalDetail: TaskDetail,
): Promise<void> {
  const original = originalDetail.task;
  const currentDetail = await expectJsonOk(page, `/api/tasks/${taskId}`) as TaskDetail;
  const current = currentDetail.task;
  if (current.description === original.description) {
    return;
  }

  const response = await requestWithCsrf(page, 'PATCH', `/api/tasks/${taskId}`, {
    title: original.title,
    description: original.description ?? '',
    goal: original.brief?.goal?.value ?? null,
    deliverable: original.brief?.deliverable?.value ?? null,
    constraints: original.brief?.constraints?.value ?? null,
    priority: taskPriorityValue(original.priority),
    plannedStartDate: original.plannedStartDate ?? original.startDate ?? null,
    plannedEndDate: original.plannedEndDate ?? original.dueDate ?? null,
    progressPercent: original.progressPercent,
    expectedVersion: current.version,
  });
  expect(response.csrfHeaderPresent).toBe(true);
  expect(response.status, response.text).toBe(200);

  const restored = await expectJsonOk(page, `/api/tasks/${taskId}`) as TaskDetail;
  expect(restored.task.description).toBe(original.description);
}

function taskPriorityValue(priority: unknown): number {
  if (typeof priority !== 'string') {
    throw new Error('FCI-04 cannot restore a non-string Task priority.');
  }
  const normalized = priority.trim().toLowerCase();
  const values = new Map<string, number>([
    ['low', 0],
    ['medium', 1],
    ['high', 2],
    ['critical', 3],
    ['urgent', 3],
  ]);
  const value = values.get(normalized);
  if (value === undefined) {
    throw new Error(`FCI-04 cannot restore unsupported Task priority ${normalized || '<empty>'}.`);
  }
  return value;
}

async function restoreTaskScope(
  page: Page,
  taskId: string,
  original: TaskExecutionScope,
): Promise<void> {
  const current = await expectJsonOk(page, `/api/tasks/${taskId}/execution-scope`) as TaskExecutionScope;
  if (original.origin === 'ProjectDefault') {
    if (current.taskOverrideVersion !== null) {
      const response = await requestWithCsrf(
        page,
        'DELETE',
        `/api/tasks/${taskId}/execution-scope-override`,
        { expectedVersion: current.taskOverrideVersion },
      );
      expect(response.status, response.text).toBe(200);
    }
    return;
  }

  const originalPolicy = original.taskOverridePolicy;
  if (!originalPolicy) {
    throw new Error('FCI-04 cannot restore a Task override without its captured policy.');
  }
  const response = await requestWithCsrf(
    page,
    'PUT',
    `/api/tasks/${taskId}/execution-scope-override`,
    {
      webEnabled: originalPolicy.webEnabled,
      projectFilesEnabled: originalPolicy.projectFilesEnabled,
      ...(originalPolicy.policyV2 ? { policyV2: originalPolicy.policyV2 } : {}),
      expectedVersion: current.taskOverrideVersion ?? 0,
    },
  );
  expect(response.status, response.text).toBe(200);
}

async function ensureAuthenticated(page: Page): Promise<void> {
  const status = await fetchFromPage(page, '/api/auth/status');
  const body = parseJson(status.text) as Record<string, unknown> | null;
  if (status.status !== 200 || body?.isAuthenticated !== true) {
    await login(page);
  }
}

async function expectJsonOk(page: Page, path: string): Promise<any> {
  const response = await fetchFromPage(page, path);
  expect(response.status, `${path}: ${response.text}`).toBe(200);
  return parseJson(response.text);
}

async function fetchFromPage(page: Page, path: string): Promise<{ status: number; text: string }> {
  return page.evaluate(async (url) => {
    const response = await fetch(url, { credentials: 'include', cache: 'no-store' });
    return { status: response.status, text: await response.text() };
  }, path);
}

async function requestWithCsrf(
  page: Page,
  method: 'POST' | 'PUT' | 'PATCH' | 'DELETE',
  path: string,
  body?: unknown,
  additionalHeaders: Readonly<Record<string, string>> = {},
): Promise<{ status: number; text: string; csrfHeaderPresent: boolean }> {
  return page.evaluate(async ({ method, path, body, additionalHeaders }) => {
    const csrfResponse = await fetch('/api/security/csrf-token', { credentials: 'include' });
    const csrf = await csrfResponse.json() as { token?: string; headerName?: string };
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      ...additionalHeaders,
    };
    if (csrf.token && csrf.headerName) { headers[csrf.headerName] = csrf.token; }
    const response = await fetch(path, {
      method,
      credentials: 'include',
      headers,
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    return {
      status: response.status,
      text: await response.text(),
      csrfHeaderPresent: Boolean(csrf.token && csrf.headerName && headers[csrf.headerName]),
    };
  }, { method, path, body, additionalHeaders });
}

function waitForApiResponse(
  page: Page,
  method: string,
  path: string,
  matchesUrl: (url: URL) => boolean = () => true,
) {
  return page.waitForResponse((response) => {
    const url = new URL(response.url());
    return response.request().method() === method && url.pathname === path && matchesUrl(url);
  });
}

function parseJson(text: string): any {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}
