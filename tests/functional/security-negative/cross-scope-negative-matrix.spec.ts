/* eslint-disable max-lines, max-lines-per-function -- FCI-07 keeps the bounded negative matrix and its authority-loss sequence visible in one owner spec. */
import { randomUUID } from 'node:crypto';
import {
  expect,
  request,
  test,
  type APIRequestContext,
  type APIResponse,
  type TestInfo,
} from '@playwright/test';

import {
  functionalFullExpansionEnabled,
  selectedFunctionalGates,
} from '../fixtures/functional-gate-selection.mjs';
import { functionalMetadata } from '../fixtures/functional-metadata.mjs';
import { loginViaApi, loginViaUi, logoutViaApi } from '../helpers/auth';
import { csrfAwareRequest } from '../helpers/csrf';
import { assertSafeDenial } from '../helpers/safe-denial';
import { assertSafeResponse } from '../helpers/safe-response';

const ALPHA_TENANT = 'security-alpha';
const BETA_TENANT = 'security-beta';
const ALPHA_OWNER_EMAIL = 'security-alpha-owner@example.test';
const ALPHA_MEMBER_EMAIL = 'security-alpha-member@example.test';
const BETA_OWNER_EMAIL = 'security-beta-owner@example.test';
const ALPHA_WORKSPACE_TITLE = 'SEC-02 Alpha Workspace Canary';
const BETA_WORKSPACE_TITLE = 'SEC-02 Beta Workspace Canary';
const ALPHA_PROJECT_TITLE = 'SEC-02 Alpha Private Project Canary';
const BETA_PROJECT_TITLE = 'SEC-02 Beta Private Project Canary';
const ALPHA_TASK_TITLE = 'SEC02 ALPHA PRIVATE TASK CANARY';
const BETA_TASK_TITLE = 'SEC02 BETA PRIVATE TASK CANARY';
const ALPHA_FILE_NAME = 'sec02-alpha-private.txt';
const BETA_FILE_NAME = 'sec02-beta-private.txt';
const BETA_NOTIFICATION_TITLE = 'SEC05 BETA NOTIFICATION CANARY';
const BETA_PROTECTED_MARKERS = [
  BETA_WORKSPACE_TITLE,
  BETA_PROJECT_TITLE,
  BETA_TASK_TITLE,
  BETA_FILE_NAME,
  'SEC02_BETA_FILE_CANARY_DO_NOT_LEAK',
  'SEC02 BETA PRIVATE CONVERSATION CANARY',
  'SEC02_BETA_MESSAGE_CANARY_DO_NOT_LEAK',
  'SEC05 BETA ANNOUNCEMENT CANARY',
  'SEC05_BETA_ANNOUNCEMENT_DO_NOT_LEAK',
  BETA_NOTIFICATION_TITLE,
  'SEC05_BETA_NOTIFICATION_DO_NOT_LEAK',
] as const;

const securityPassword = process.env.COGLATAS_SECURITY_CI_PASSWORD ?? '';
const baseURL = process.env.PLAYWRIGHT_BASE_URL ?? '';

test.describe('FCI-07 real-stack authorization negative matrix', () => {
  test.setTimeout(120_000);

  test.beforeAll(() => {
    if (process.env.COGLATAS_REAL_BACKEND_SMOKE !== '1' || process.env.COGLATAS_SECURITY_CI_FIXTURE_ENABLED !== 'true') {
      throw new Error('FCI-07 requires the isolated Security CI Functional Compose profile.');
    }
    if (!baseURL || !securityPassword) {
      throw new Error('FCI-07 requires PLAYWRIGHT_BASE_URL and the synthetic COGLATAS_SECURITY_CI_PASSWORD.');
    }
  });

  test(
    'FUNC-AUTHZ-001 enforces anonymous, role, CSRF, and cleared-session boundaries',
    functionalMetadata({
      journeyId: 'FUNC-AUTHZ-001',
      gates: ['functional-fast', 'functional-full', 'functional-extended'],
      domains: ['auth', 'workspace', 'security-negative'],
      priority: 'p0',
      backend: 'real',
      polarity: 'negative',
      negativeAuthz: true,
    }),
    async ({ browserName }, testInfo) => {
      expect(browserName).toBe('chromium');
      const anonymousApi = await createTenantApi(ALPHA_TENANT);
      const memberApi = await createTenantApi(ALPHA_TENANT);
      const ownerApi = await createTenantApi(ALPHA_TENANT);
      const evidence: Record<string, unknown> = {
        journeyId: 'FUNC-AUTHZ-001',
        selectedGates: selectedFunctionalGates(),
        anonymousWorkspaceStatus: null,
        memberAdminStatus: null,
        missingCsrfStatus: null,
        invalidCsrfStatus: null,
        postLogoutStatus: null,
      };

      try {
        const anonymous = await anonymousApi.get('/api/workspaces');
        evidence.anonymousWorkspaceStatus = (
          await assertSafeDenial(anonymous, {
            label: 'FCI-07 anonymous Workspace list',
            expectedStatus: 401,
            forbiddenMarkers: [ALPHA_WORKSPACE_TITLE, ALPHA_PROJECT_TITLE, ALPHA_TASK_TITLE, ALPHA_FILE_NAME],
          })
        ).status;

        await loginViaApi(memberApi, { email: ALPHA_MEMBER_EMAIL, password: securityPassword });
        const memberAdmin = await memberApi.get('/api/admin/invites');
        evidence.memberAdminStatus = (
          await assertSafeDenial(memberAdmin, {
            label: 'FCI-07 ordinary member admin read',
            expectedStatus: 403,
          })
        ).status;

        await loginViaApi(ownerApi, { email: ALPHA_OWNER_EMAIL, password: securityPassword });
        const alpha = await resolveCoreGraph(ownerApi, {
          workspaceTitle: ALPHA_WORKSPACE_TITLE,
          projectTitle: ALPHA_PROJECT_TITLE,
          taskTitle: ALPHA_TASK_TITLE,
          fileName: ALPHA_FILE_NAME,
        });
        const membersBefore = await readManagedWorkspaceMembers(ownerApi, alpha.workspaceId);
        const alphaMember = membersBefore.find((member) => readString(member, 'email', 'Email') === ALPHA_MEMBER_EMAIL);
        if (!alphaMember) {
          throw new Error('FCI-07 Alpha member fixture was not visible to the Workspace owner.');
        }
        const alphaMemberId = requireString(alphaMember, 'userId', 'UserId');

        const missingCsrf = await ownerApi.post(`/api/workspaces/${alpha.workspaceId}/members`, {
          data: { userId: alphaMemberId, role: 3 },
        });
        evidence.missingCsrfStatus = (
          await assertSafeDenial(missingCsrf, {
            label: 'FCI-07 missing-CSRF Workspace mutation',
            expectedStatus: 403,
          })
        ).status;
        expect((await readManagedWorkspaceMembers(ownerApi, alpha.workspaceId)).length).toBe(membersBefore.length);

        if (functionalFullExpansionEnabled()) {
          const invalidCsrf = await ownerApi.post(`/api/workspaces/${alpha.workspaceId}/members`, {
            headers: singleHeader('X-CSRF-Token', 'fci07-intentionally-invalid'),
            data: { userId: alphaMemberId, role: 3 },
          });
          evidence.invalidCsrfStatus = (
            await assertSafeDenial(invalidCsrf, {
              label: 'FCI-07 invalid-CSRF Workspace mutation',
              expectedStatus: 403,
            })
          ).status;
          expect((await readManagedWorkspaceMembers(ownerApi, alpha.workspaceId)).length).toBe(membersBefore.length);
        }

        await logoutViaApi(memberApi);
        const postLogout = await memberApi.get('/api/workspaces');
        evidence.postLogoutStatus = (
          await assertSafeDenial(postLogout, {
            label: 'FCI-07 protected API after logout',
            expectedStatus: 401,
            forbiddenMarkers: [ALPHA_WORKSPACE_TITLE, ALPHA_PROJECT_TITLE, ALPHA_TASK_TITLE, ALPHA_FILE_NAME],
          })
        ).status;
      } finally {
        await attachEvidence(testInfo, 'fci-07-authz-001-evidence.json', evidence);
        await Promise.all([anonymousApi.dispose(), memberApi.dispose(), ownerApi.dispose()]);
      }
    },
  );

  test(
    'FUNC-AUTHZ-002 rejects foreign identifiers and reauthorizes stale UI after membership revoke',
    functionalMetadata({
      journeyId: 'FUNC-AUTHZ-002',
      gates: ['functional-fast', 'functional-full', 'functional-extended'],
      domains: ['auth', 'workspace', 'task', 'files', 'notification', 'security-negative'],
      priority: 'p0',
      backend: 'real',
      polarity: 'negative',
      negativeAuthz: true,
    }),
    async ({ page }, testInfo) => {
      const alphaOwnerApi = await createTenantApi(ALPHA_TENANT);
      const alphaMemberApi = await createTenantApi(ALPHA_TENANT);
      const betaOwnerApi = await createTenantApi(BETA_TENANT);
      const evidence: Record<string, unknown> = {
        journeyId: 'FUNC-AUTHZ-002',
        selectedGates: selectedFunctionalGates(),
        fullExpansionEnabled: functionalFullExpansionEnabled(),
        foreignWorkspaceStatus: null,
        foreignProjectStatus: null,
        foreignTaskStatus: null,
        foreignFileStatus: null,
        sameTenantWorkspaceStatus: null,
        sameTenantProjectStatus: null,
        sameTenantTaskStatus: null,
        sameTenantFileStatus: null,
        foreignDirectRouteDenied: false,
        notificationOpenStatus: null,
        existenceOracleStatusAligned: null,
        taskDetailNoStore: false,
        revokeStatus: null,
        postRevokeTaskStatus: null,
        backForwardCacheCleared: false,
        staleUiCleared: false,
      };

      try {
        await loginViaApi(alphaOwnerApi, { email: ALPHA_OWNER_EMAIL, password: securityPassword });
        await loginViaApi(alphaMemberApi, { email: ALPHA_MEMBER_EMAIL, password: securityPassword });
        await loginViaApi(betaOwnerApi, { email: BETA_OWNER_EMAIL, password: securityPassword });

        const alpha = await resolveCoreGraph(alphaOwnerApi, {
          workspaceTitle: ALPHA_WORKSPACE_TITLE,
          projectTitle: ALPHA_PROJECT_TITLE,
          taskTitle: ALPHA_TASK_TITLE,
          fileName: ALPHA_FILE_NAME,
        });
        const beta = await resolveCoreGraph(betaOwnerApi, {
          workspaceTitle: BETA_WORKSPACE_TITLE,
          projectTitle: BETA_PROJECT_TITLE,
          taskTitle: BETA_TASK_TITLE,
          fileName: BETA_FILE_NAME,
        });
        const sameTenant = await createSameTenantRestrictedGraph(alphaOwnerApi);
        const sameTenantMembers = await readManagedWorkspaceMembers(alphaOwnerApi, sameTenant.workspaceId);
        expect(sameTenantMembers.some((member) => readString(member, 'email', 'Email') === ALPHA_MEMBER_EMAIL)).toBe(false);

        const foreignWorkspace = await alphaMemberApi.get(`/api/workspaces/${beta.workspaceId}`);
        evidence.foreignWorkspaceStatus = (
          await assertSafeDenial(foreignWorkspace, {
            label: 'FCI-07 cross-Tenant Workspace ID swap',
            expectedStatus: [403, 404],
            forbiddenMarkers: BETA_PROTECTED_MARKERS,
          })
        ).status;

        const foreignProject = await alphaMemberApi.get(`/api/projects/${beta.projectId}`);
        evidence.foreignProjectStatus = (
          await assertSafeDenial(foreignProject, {
            label: 'FCI-07 cross-Tenant Project ID swap',
            expectedStatus: [403, 404],
            forbiddenMarkers: BETA_PROTECTED_MARKERS,
          })
        ).status;

        const foreignTask = await alphaMemberApi.get(`/api/tasks/${beta.taskId}`);
        const foreignTaskDenial = await assertSafeDenial(foreignTask, {
          label: 'FCI-07 cross-Tenant Task ID swap',
          expectedStatus: [403, 404],
          forbiddenMarkers: BETA_PROTECTED_MARKERS,
        });
        evidence.foreignTaskStatus = foreignTaskDenial.status;

        evidence.foreignFileStatus = await assertFileMetadataDenial(alphaOwnerApi, beta.fileId, {
          label: 'FCI-07 cross-Tenant File ID swap',
          forbiddenMarkers: BETA_PROTECTED_MARKERS,
        });

        const sameTenantWorkspace = await alphaMemberApi.get(`/api/workspaces/${sameTenant.workspaceId}`);
        evidence.sameTenantWorkspaceStatus = (
          await assertSafeDenial(sameTenantWorkspace, {
            label: 'FCI-07 same-Tenant cross-Workspace ID swap',
            expectedStatus: [403, 404],
            forbiddenMarkers: sameTenant.protectedMarkers,
          })
        ).status;

        const sameTenantProject = await alphaMemberApi.get(`/api/projects/${sameTenant.projectId}`);
        evidence.sameTenantProjectStatus = (
          await assertSafeDenial(sameTenantProject, {
            label: 'FCI-07 same-Tenant cross-Workspace Project ID swap',
            expectedStatus: [403, 404],
            forbiddenMarkers: sameTenant.protectedMarkers,
          })
        ).status;

        const sameTenantTask = await alphaMemberApi.get(`/api/tasks/${sameTenant.taskId}`);
        evidence.sameTenantTaskStatus = (
          await assertSafeDenial(sameTenantTask, {
            label: 'FCI-07 same-Tenant cross-Workspace Task ID swap',
            expectedStatus: [403, 404],
            forbiddenMarkers: sameTenant.protectedMarkers,
          })
        ).status;

        evidence.sameTenantFileStatus = await assertFileMetadataDenial(alphaMemberApi, sameTenant.fileId, {
          label: 'FCI-07 same-Tenant cross-Workspace File ID swap',
          forbiddenMarkers: sameTenant.protectedMarkers,
        });
        evidence.fileMetadataOracleStatusAligned = true;

        await page.setExtraHTTPHeaders(singleHeader('X-Tenant-Slug', ALPHA_TENANT));
        await loginViaUi(page, { email: ALPHA_MEMBER_EMAIL, password: securityPassword });

        await page.goto(`/app/projects/${beta.projectId}/tasks/${beta.taskId}`);
        await expect(page.getByTestId('permission-denied-state')).toBeVisible();
        await expect(page.getByRole('heading', { name: BETA_TASK_TITLE })).toHaveCount(0);
        evidence.foreignDirectRouteDenied = true;

        if (functionalFullExpansionEnabled()) {
          const betaNotificationId = await resolveNotificationId(betaOwnerApi, BETA_NOTIFICATION_TITLE);
          const foreignNotificationOpen = await csrfAwareRequest(
            alphaOwnerApi,
            'POST',
            `/api/notifications/${betaNotificationId}/open`,
          );
          const notificationDenial = await assertSafeDenial(foreignNotificationOpen, {
            label: 'FCI-07 foreign Notification deep-link open',
            expectedStatus: 404,
            forbiddenMarkers: BETA_PROTECTED_MARKERS,
          });
          evidence.notificationOpenStatus = notificationDenial.status;

          const unknownTask = await alphaMemberApi.get(`/api/tasks/${randomUUID()}`);
          const unknownTaskDenial = await assertSafeDenial(unknownTask, {
            label: 'FCI-07 unknown Task oracle control',
            expectedStatus: foreignTaskDenial.status,
            forbiddenMarkers: BETA_PROTECTED_MARKERS,
          });
          expect(Math.abs(unknownTaskDenial.bodyBytes - foreignTaskDenial.bodyBytes)).toBeLessThanOrEqual(256);
          evidence.existenceOracleStatusAligned = true;
        }

        const taskDetailResponsePromise = page.waitForResponse((response) =>
          response.request().method() === 'GET' &&
          new URL(response.url()).pathname === `/api/tasks/${alpha.taskId}`,
        );
        await page.goto(`/app/projects/${alpha.projectId}/tasks/${alpha.taskId}`);
        const taskDetailResponse = await taskDetailResponsePromise;
        expect(taskDetailResponse.status()).toBe(200);
        const taskDetailHeaders = taskDetailResponse.headers();
        expect(taskDetailHeaders['cache-control'] ?? '').toContain('no-store');
        expect(taskDetailHeaders.pragma).toContain('no-cache');
        expect(taskDetailHeaders.expires).toBe('0');
        evidence.taskDetailNoStore = true;
        await expect(page.getByTestId('task-detail-page')).toBeVisible();
        await expect(page.getByRole('heading', { name: ALPHA_TASK_TITLE })).toBeVisible();

        await page.goto(`/app/projects/${beta.projectId}/tasks/${beta.taskId}`);
        await expect(page.getByTestId('permission-denied-state')).toBeVisible();

        const members = await readManagedWorkspaceMembers(alphaOwnerApi, alpha.workspaceId);
        const alphaMember = members.find((member) => readString(member, 'email', 'Email') === ALPHA_MEMBER_EMAIL);
        if (!alphaMember) {
          throw new Error('FCI-07 could not resolve the Alpha member before revocation.');
        }
        const alphaMemberId = requireString(alphaMember, 'userId', 'UserId');
        const revoke = await csrfAwareRequest(
          alphaOwnerApi,
          'DELETE',
          `/api/workspaces/${alpha.workspaceId}/members/${alphaMemberId}`,
        );
        await assertSafeResponse(revoke, { label: 'FCI-07 Workspace membership revoke', expectedStatus: 200 });
        evidence.revokeStatus = revoke.status();

        const postRevokeTask = await alphaMemberApi.get(`/api/tasks/${alpha.taskId}`);
        evidence.postRevokeTaskStatus = (
          await assertSafeDenial(postRevokeTask, {
            label: 'FCI-07 Task read after Workspace membership revoke',
            expectedStatus: [403, 404],
            forbiddenMarkers: [ALPHA_TASK_TITLE, ALPHA_PROJECT_TITLE, ALPHA_FILE_NAME],
          })
        ).status;

        await page.goBack();
        await expect(page).toHaveURL(new RegExp(`/app/projects/${alpha.projectId}/tasks/${alpha.taskId}$`));
        await expect(page.getByTestId('permission-denied-state')).toBeVisible();
        await expect(page.getByRole('heading', { name: ALPHA_TASK_TITLE })).toHaveCount(0);
        evidence.backForwardCacheCleared = true;

        await page.reload();
        await expect(page.getByTestId('permission-denied-state')).toBeVisible();
        await expect(page.getByRole('heading', { name: ALPHA_TASK_TITLE })).toHaveCount(0);
        evidence.staleUiCleared = true;
      } finally {
        await attachEvidence(testInfo, 'fci-07-authz-002-evidence.json', evidence);
        await Promise.all([alphaOwnerApi.dispose(), alphaMemberApi.dispose(), betaOwnerApi.dispose()]);
      }
    },
  );
});

interface CoreFixtureExpectation {
  workspaceTitle: string;
  projectTitle: string;
  taskTitle: string;
  fileName: string;
}

interface CoreFixtureGraph {
  workspaceId: string;
  projectId: string;
  taskId: string;
  fileId: string;
}

interface SameTenantRestrictedGraph extends CoreFixtureGraph {
  protectedMarkers: readonly string[];
}

async function assertFileMetadataDenial(
  api: APIRequestContext,
  fileId: string,
  options: { label: string; forbiddenMarkers: readonly string[] },
): Promise<number> {
  // FilesController preserves the existing 400 contract for denied and absent
  // metadata; both must carry the same canonical redacted error envelope.
  const denied = await assertFileMetadataDeniedResponse(await api.get(`/api/files/${fileId}`), options);
  const absent = await assertFileMetadataDeniedResponse(await api.get(`/api/files/${randomUUID()}`), {
    ...options,
    label: `${options.label} unknown File oracle control`,
  });
  expect(absent.status).toBe(denied.status);
  expect(Math.abs(absent.bodyBytes - denied.bodyBytes)).toBeLessThanOrEqual(256);
  return denied.status;
}

async function assertFileMetadataDeniedResponse(
  response: APIResponse,
  options: { label: string; forbiddenMarkers: readonly string[] },
): Promise<{ status: number; bodyBytes: number }> {
  const denial = await assertSafeDenial(response, { ...options, expectedStatus: 400, maxBodyBytes: 2048 });
  const envelope = asRecord(await response.json(), `${options.label} envelope`);
  const error = asRecord(envelope.error, `${options.label} error`);
  const validCorrelation = (value: unknown): boolean =>
    typeof value === 'string' && value.length > 0 && value.length <= 256;
  const canonical =
    Object.keys(envelope).sort().join(',') === 'error,requestId,status,traceId' &&
    envelope.status === 400 &&
    validCorrelation(envelope.requestId) &&
    validCorrelation(envelope.traceId) &&
    Object.keys(error).sort().join(',') === 'code,details,message,redactionApplied,target' &&
    error.code === 'FileMetadataFailed' &&
    error.message === 'The request could not be completed.' &&
    error.target === null &&
    Array.isArray(error.details) && error.details.length === 0 &&
    error.redactionApplied === true;
  if (!canonical) {
    // Report only the failed contract, never an unexpected protected payload.
    throw new Error(`${options.label} did not return the canonical redacted File metadata denial.`);
  }
  return denial;
}

async function createTenantApi(tenantSlug: string): Promise<APIRequestContext> {
  return request.newContext({
    baseURL,
    extraHTTPHeaders: singleHeader('X-Tenant-Slug', tenantSlug),
  });
}

async function createSameTenantRestrictedGraph(api: APIRequestContext): Promise<SameTenantRestrictedGraph> {
  const token = randomUUID();
  const marker = token.slice(0, 8);
  const workspaceTitle = `FCI-07 Alpha Restricted Workspace ${marker}`;
  const projectTitle = `FCI-07 Alpha Restricted Project ${marker}`;
  const taskTitle = `FCI-07 ALPHA RESTRICTED TASK ${marker}`;
  const fileName = `fci07-alpha-restricted-${marker}.txt`;
  const fileBody = `FCI07_ALPHA_RESTRICTED_FILE_${marker}_DO_NOT_LEAK`;

  const workspaceCreate = await csrfAwareRequest(api, 'POST', '/api/workspaces', {
    headers: singleHeader('Idempotency-Key', `fci07-workspace-${token}`),
    data: {
      name: workspaceTitle,
      description: 'FCI-07 isolated same-Tenant authorization boundary.',
      icon: null,
    },
  });
  await assertSafeResponse(workspaceCreate, { label: 'FCI-07 same-Tenant Workspace create', expectedStatus: 201 });
  const workspaceData = readEnvelopeData(await workspaceCreate.json(), 'same-Tenant Workspace create');
  const workspaceId = requireString(workspaceData, 'id', 'Id');

  const projectCreate = await csrfAwareRequest(api, 'POST', `/api/workspaces/${workspaceId}/projects`, {
    headers: singleHeader('Idempotency-Key', `fci07-project-${token}`),
    data: {
      title: projectTitle,
      description: null,
      groupId: null,
      visibility: 1,
      startDate: null,
      endDate: null,
    },
  });
  await assertSafeResponse(projectCreate, { label: 'FCI-07 same-Tenant Project create', expectedStatus: 201 });
  const projectData = readEnvelopeData(await projectCreate.json(), 'same-Tenant Project create');
  const projectId = requireString(projectData, 'id', 'Id');
  const projectVersion = requireNumber(projectData, 'versionNo', 'VersionNo');

  const activation = await csrfAwareRequest(api, 'POST', `/api/projects/${projectId}/activate`, {
    data: { expectedVersion: projectVersion },
  });
  await assertSafeResponse(activation, { label: 'FCI-07 same-Tenant Project activation', expectedStatus: 200 });

  const taskCreate = await csrfAwareRequest(api, 'POST', `/api/projects/${projectId}/tasks/create`, {
    headers: singleHeader('Idempotency-Key', `fci07-task-${token}`),
    data: {
      title: taskTitle,
      priority: 1,
      sourceScopeMode: 'Inherit',
    },
  });
  await assertSafeResponse(taskCreate, { label: 'FCI-07 same-Tenant Task create', expectedStatus: 201 });
  const taskData = readEnvelopeData(await taskCreate.json(), 'same-Tenant Task create');
  const taskId = requireString(taskData, 'taskId', 'TaskId');

  const fileCreate = await csrfAwareRequest(api, 'POST', '/api/files', {
    multipart: Object.fromEntries([
      ['OwnerType', 'Workspace'],
      ['OwnerId', workspaceId],
      ['File', { name: fileName, mimeType: 'text/plain', buffer: Buffer.from(fileBody, 'utf8') }],
    ]),
  });
  await assertSafeResponse(fileCreate, { label: 'FCI-07 same-Tenant File create', expectedStatus: 200 });
  const fileData = asRecord(await fileCreate.json(), 'same-Tenant File create');
  const fileId = requireString(fileData, 'fileObjectId', 'FileObjectId', 'id', 'Id');

  return {
    workspaceId,
    projectId,
    taskId,
    fileId,
    protectedMarkers: [workspaceTitle, projectTitle, taskTitle, fileName, fileBody],
  };
}

async function resolveCoreGraph(
  api: APIRequestContext,
  expected: CoreFixtureExpectation,
): Promise<CoreFixtureGraph> {
  const workspacesResponse = await api.get('/api/workspaces');
  await assertSafeResponse(workspacesResponse, { label: 'FCI-07 Workspace fixture list', expectedStatus: 200 });
  const workspaces = asArray(await workspacesResponse.json(), 'Workspace list');
  const workspace = workspaces.find((item) => readString(item, 'name', 'Name') === expected.workspaceTitle);
  if (!workspace) {
    throw new Error('FCI-07 expected Workspace fixture was not found.');
  }
  const workspaceId = requireString(workspace, 'id', 'Id');

  const projectsResponse = await api.get(`/api/projects?workspaceId=${workspaceId}&page=1&pageSize=100`);
  await assertSafeResponse(projectsResponse, { label: 'FCI-07 Project fixture list', expectedStatus: 200 });
  const projects = readItems(await projectsResponse.json(), 'Project list');
  const project = projects.find((item) => readString(item, 'title', 'Title', 'name', 'Name') === expected.projectTitle);
  if (!project) {
    throw new Error('FCI-07 expected Project fixture was not found.');
  }
  const projectId = requireString(project, 'id', 'Id');

  const tasksResponse = await api.get(`/api/projects/${projectId}/tasks?page=1&pageSize=100`);
  await assertSafeResponse(tasksResponse, { label: 'FCI-07 Task fixture list', expectedStatus: 200 });
  const tasks = readItems(await tasksResponse.json(), 'Task list');
  const task = tasks.find((item) => readString(item, 'title', 'Title') === expected.taskTitle);
  if (!task) {
    throw new Error('FCI-07 expected Task fixture was not found.');
  }
  const taskId = requireString(task, 'id', 'Id');

  const taskFilesResponse = await api.get(`/api/tasks/${taskId}/files?page=1&pageSize=100`);
  await assertSafeResponse(taskFilesResponse, { label: 'FCI-07 Task File fixture list', expectedStatus: 200 });
  const taskFilesPage = asRecord(await taskFilesResponse.json(), 'Task File fixture page');
  const taskFiles = readItems(taskFilesPage, 'Task File fixture list');
  // SEC-02 seeds exactly one attachment for this uniquely resolved Task.
  // FileMetadata protects its filename even for a record-authorized owner.
  expect(requireNumber(taskFilesPage, 'totalCount', 'TotalCount')).toBe(1);
  expect(taskFiles).toHaveLength(1);
  const [file] = taskFiles;
  expect(readString(file, 'fileName', 'FileName')).toBe('[redacted:file]');
  const fileId = requireString(file, 'fileObjectId', 'FileObjectId');
  const fileResponse = await api.get(`/api/files/${fileId}`);
  await assertSafeResponse(fileResponse, { label: 'FCI-07 Task File fixture identity', expectedStatus: 200 });
  const fileData = asRecord(await fileResponse.json(), 'Task File fixture identity');
  expect(requireString(fileData, 'id', 'Id')).toBe(fileId);
  expect(requireString(fileData, 'workspaceId', 'WorkspaceId')).toBe(workspaceId);
  expect(requireString(fileData, 'projectId', 'ProjectId')).toBe(projectId);
  expect(readString(fileData, 'originalFileName', 'OriginalFileName')).toBe('[redacted:file]');
  expect(readString(fileData, 'originalFileName', 'OriginalFileName')).not.toBe(expected.fileName);
  expect(readString(fileData, 'contentType', 'ContentType')).toBe('text/plain');
  expect(readString(fileData, 'status', 'Status')).toBe('Active');

  return { workspaceId, projectId, taskId, fileId };
}

async function readManagedWorkspaceMembers(api: APIRequestContext, workspaceId: string): Promise<Record<string, unknown>[]> {
  const response = await api.get(`/api/workspaces/${workspaceId}/members/management`);
  await assertSafeResponse(response, { label: 'FCI-07 managed Workspace member list', expectedStatus: 200 });
  return asArray(await response.json(), 'Managed Workspace member list');
}

async function resolveNotificationId(api: APIRequestContext, title: string): Promise<string> {
  const response = await api.get('/api/notifications?page=1&pageSize=100');
  await assertSafeResponse(response, { label: 'FCI-07 Notification list', expectedStatus: 200 });
  const items = readItems(await response.json(), 'Notification list');
  const notification = items.find((item) => readString(item, 'title', 'Title') === title);
  if (!notification) {
    throw new Error('FCI-07 expected Notification fixture was not found.');
  }
  return requireString(notification, 'id', 'Id');
}

function readEnvelopeData(value: unknown, label: string): Record<string, unknown> {
  const envelope = asRecord(value, `${label} envelope`);
  return asRecord(envelope.data ?? envelope.Data, `${label} data`);
}

function asArray(value: unknown, label: string): Record<string, unknown>[] {
  if (!Array.isArray(value)) {
    throw new Error(`${label} was not an array.`);
  }
  return value.map((item) => asRecord(item, `${label} item`));
}

function readItems(value: unknown, label: string): Record<string, unknown>[] {
  const record = asRecord(value, label);
  const items = record.items ?? record.Items;
  return asArray(items, `${label} items`);
}

function asRecord(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} was not a JSON object.`);
  }
  return value as Record<string, unknown>;
}

function readString(record: Record<string, unknown>, ...keys: string[]): string | null {
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'string' && value.length > 0) {
      return value;
    }
  }
  return null;
}

function requireString(record: Record<string, unknown>, ...keys: string[]): string {
  const value = readString(record, ...keys);
  if (!value) {
    throw new Error(`FCI-07 required string field was missing: ${keys.join(' / ')}.`);
  }
  return value;
}

function requireNumber(record: Record<string, unknown>, ...keys: string[]): number {
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'number' && Number.isFinite(value)) {
      return value;
    }
  }
  throw new Error(`FCI-07 required numeric field was missing: ${keys.join(' / ')}.`);
}

function singleHeader(name: string, value: string): Record<string, string> {
  return Object.fromEntries([[name, value]]);
}

async function attachEvidence(testInfo: TestInfo, name: string, evidence: Record<string, unknown>): Promise<void> {
  await testInfo.attach(name, {
    body: JSON.stringify(evidence, null, 2),
    contentType: 'application/json',
  });
}
