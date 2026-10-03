/* eslint-disable max-lines, max-lines-per-function -- FCI-05 is an end-to-end owner journey; keep the complete mutation/cleanup contract visible in one spec. */
import { randomUUID } from 'node:crypto';
import { expect, type APIRequestContext, type APIResponse, type Response, test } from '@playwright/test';

import { functionalMetadata } from '../fixtures/functional-metadata.mjs';
import { functionalFullExpansionEnabled } from '../fixtures/functional-gate-selection.mjs';
import { loginViaApi } from '../helpers/auth';
import { csrfAwareRequest } from '../helpers/csrf';
import { safeResponsePreview } from '../helpers/safe-response';
import { runFilesLifecycle } from './files-lifecycle-steps';

const smokeEmail = process.env.COGLATAS_BROWSER_SMOKE_EMAIL ?? '';
const smokePassword = process.env.COGLATAS_BROWSER_SMOKE_PASSWORD ?? '';
const smokeWorkspaceTitle = 'Browser Smoke Workspace';

test.describe('FCI-05 Files real-backend fast journey', () => {
  test.setTimeout(120_000);

  test.beforeAll(() => {
    if (process.env.COGLATAS_REAL_BACKEND_SMOKE !== '1') {
      throw new Error('FCI-05 requires COGLATAS_REAL_BACKEND_SMOKE=1 and the canonical Functional Compose harness.');
    }
    if (!process.env.PLAYWRIGHT_BASE_URL || !smokeEmail.toLowerCase().endsWith('@example.test') || !smokePassword) {
      throw new Error('FCI-05 requires the isolated real-backend Functional fixture profile.');
    }
  });

  test.beforeEach(({ browserName }, testInfo) => {
    if (browserName !== 'chromium' || testInfo.project.name !== 'functional-chromium') {
      throw new Error('FCI-05 requires the canonical functional-chromium project; an owner cannot pass by skipping.');
    }
  });

  test(
    'FUNC-FILE-002 uploads, browses, inspects, downloads, reloads, and safely removes one FileObject',
    functionalMetadata({
      journeyId: 'FUNC-FILE-002',
      gates: ['functional-fast', 'functional-full', 'functional-extended'],
      domains: ['auth', 'workspace', 'files'],
      priority: 'p0',
      backend: 'real',
      polarity: 'positive',
    }),
    async ({ page }, testInfo) => {
      const api = page.context().request;
      const runToken = randomUUID();
      const fileName = `fci05-${runToken}.txt`;
      const failedFileName = `fci05-invalid-${runToken}.txt`;
      const fileContent = 'FCI-05 deterministic synthetic Files owner content.\n';
      let fileObjectId: string | null = null;
      let workspaceId: string | null = null;
      let uploadAttempted = false;
      let cleanupSucceeded = false;

      const evidence: Record<string, unknown> = {
        journeyId: 'FUNC-FILE-002',
        fileName,
        workspaceId: null,
        fileObjectId: null,
        uploadStatus: null,
        freshReadStatus: null,
        downloadStatus: null,
        reloadReadStatus: null,
        sharingAccessState: null,
        failedMutationStatus: null,
        deletedReadStatus: null,
        deletedGrantStatus: null,
        cleanupSucceeded: false,
        fullExpansion: functionalFullExpansionEnabled(),
      };

      try {
        await loginViaApi(api, { email: smokeEmail, password: smokePassword });
        workspaceId = await resolveWorkspaceId(api, smokeWorkspaceTitle);
        evidence.workspaceId = workspaceId;

        // A rejected upload must not manufacture a FileObject or storage-visible metadata.
        const rejectedUpload = await csrfAwareRequest(api, 'POST', '/api/files', {
          multipart: Object.fromEntries([
            ['OwnerType', 'Workspace'],
            ['OwnerId', workspaceId],
            ['File', { name: failedFileName, mimeType: 'text/plain', buffer: Buffer.alloc(0) }],
          ]),
        });
        assertSafeResponse(rejectedUpload, { label: 'FCI-05 rejected upload', expectedStatus: 400 });
        evidence.failedMutationStatus = rejectedUpload.status();
        expect(await fileNamesForWorkspace(api, workspaceId)).not.toContain(failedFileName);

        // NavigationEnd commits Workspace scope after the page component mounts.
        // Wait for its scoped inventory read before acting on the uploader.
        const initialFileListPromise = page.waitForResponse((response) =>
          response.request().method() === 'GET' &&
          new URL(response.url()).pathname === '/api/files' &&
          new URL(response.url()).searchParams.get('workspaceId') === workspaceId,
        );
        await page.goto(`/app/workspaces/${workspaceId}/files`);
        assertSafeResponse(await initialFileListPromise, { label: 'FCI-05 initial scoped inventory', expectedStatus: 200 });
        await expect(page.getByTestId('files-page')).toBeVisible();
        await expect(page.locator('app-coglatas-file-uploader input[type="file"]')).toBeEnabled();

        const uploadResponsePromise = page.waitForResponse((response) =>
          response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/files',
        );
        uploadAttempted = true;
        await page.locator('app-coglatas-file-uploader input[type="file"]').setInputFiles({
          name: fileName,
          mimeType: 'text/plain',
          buffer: Buffer.from(fileContent, 'utf8'),
        });
        const uploadResponse = await uploadResponsePromise;
        assertSafeResponse(uploadResponse, { label: 'FCI-05 UI upload', expectedStatus: 200 });
        evidence.uploadStatus = uploadResponse.status();

        const uploadBody = asRecord(await uploadResponse.json(), 'File upload response');
        fileObjectId = requireStringField(uploadBody, 'fileObjectId', 'FileObjectId');
        evidence.fileObjectId = fileObjectId;

        const freshRead = await api.get(`/api/files/${fileObjectId}`);
        assertSafeResponse(freshRead, { label: 'FCI-05 fresh FileObject read', expectedStatus: 200 });
        const freshBody = asRecord(await freshRead.json(), 'fresh FileObject read');
        expect(requireStringField(freshBody, 'id', 'Id')).toBe(fileObjectId);
        expect(requireStringField(freshBody, 'originalFileName', 'OriginalFileName')).toBe(fileName);
        expect(freshBody.sizeBytes).toBe(Buffer.byteLength(fileContent, 'utf8'));
        expect(freshBody.contentType).toBe('text/plain');
        assertNoStorageLeak(freshBody);
        evidence.freshReadStatus = freshRead.status();

        const listAfterUpload = await readFileList(api, workspaceId);
        const uploadedListItem = listAfterUpload.find((item) =>
          readOptionalString(item, 'fileObjectId', 'FileObjectId') === fileObjectId,
        );
        if (!uploadedListItem) {
          throw new Error('Fresh list did not contain the uploaded FileObject.');
        }
        expect(readOptionalString(uploadedListItem, 'originalFileName', 'OriginalFileName')).toBe(fileName);
        assertNoStorageLeak(uploadedListItem);

        const previewAction = page.getByRole('button', { name: fileName, exact: true });
        await expect(previewAction).toBeVisible({ timeout: 20_000 });
        await previewAction.click();
        const inspector = page.getByTestId('files-preview-pane');
        await expect(inspector).toBeVisible();
        await expect(inspector.getByRole('heading', { name: fileName })).toBeVisible();

        const sharingResponse = await api.get(`/api/files/${fileObjectId}/sharing`);
        assertSafeResponse(sharingResponse, { label: 'FCI-05 File sharing read', expectedStatus: 200 });
        const sharing = asRecord(await sharingResponse.json(), 'File sharing response');
        const accessState = requireStringField(sharing, 'accessState', 'AccessState');
        evidence.sharingAccessState = accessState;
        await expect(inspector.getByTestId('files-preview-access-state')).toHaveText(new RegExp(`^${accessState}$`, 'iu'));
        assertNoStorageLeak(sharing);

        await inspector.getByTestId('files-inspector-tab-details').click();
        await expect(inspector.getByTestId('files-inspector-panel-details')).toBeVisible();
        await expect(inspector.getByRole('heading', { name: fileName })).toBeVisible();
        await inspector.getByTestId('files-inspector-tab-preview').click();
        await inspector.getByTestId('files-preview-more').click();

        const grantResponsePromise = page.waitForResponse((response) =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === `/api/files/${fileObjectId}/download-grants`,
        );
        const downloadResponsePromise = page.waitForResponse((response) => {
          const path = new URL(response.url()).pathname;
          return response.request().method() === 'POST' &&
            /^\/api\/file-download-grants\/[0-9a-f-]{36}\/download$/iu.test(path);
        });
        await inspector.getByTestId('files-preview-download').click();
        const [grantResponse, downloadResponse] = await Promise.all([grantResponsePromise, downloadResponsePromise]);
        assertSafeResponse(grantResponse, { label: 'FCI-05 UI download grant', expectedStatus: 200 });
        assertSafeResponse(downloadResponse, { label: 'FCI-05 UI download', expectedStatus: 200 });
        const grant = asRecord(await grantResponse.json(), 'download grant');
        expect(requireStringField(grant, 'fileObjectId', 'FileObjectId')).toBe(fileObjectId);
        expect((await downloadResponse.body()).equals(Buffer.from(fileContent, 'utf8')), 'Downloaded bytes match the synthetic fixture').toBe(true);
        expect(downloadResponse.headers()['content-type']).toContain('text/plain');
        evidence.downloadStatus = downloadResponse.status();

        await page.reload();
        await expect(page.getByTestId('files-page')).toBeVisible();
        await expect(page.getByRole('button', { name: fileName, exact: true })).toBeVisible({ timeout: 20_000 });

        const reloadRead = await api.get(`/api/files/${fileObjectId}`);
        assertSafeResponse(reloadRead, { label: 'FCI-05 reload-backed FileObject read', expectedStatus: 200 });
        const reloadBody = asRecord(await reloadRead.json(), 'reload-backed FileObject read');
        expect(requireStringField(reloadBody, 'id', 'Id')).toBe(fileObjectId);
        expect(requireStringField(reloadBody, 'originalFileName', 'OriginalFileName')).toBe(fileName);
        assertNoStorageLeak(reloadBody);
        evidence.reloadReadStatus = reloadRead.status();

        if (functionalFullExpansionEnabled()) {
          const lifecycleEvidence = await runFilesLifecycle({
            api, page, workspaceId, fileObjectId, fileName, content: Buffer.from(fileContent, 'utf8'),
          });
          Object.assign(evidence, lifecycleEvidence);
        }

        const deleteResponse = await csrfAwareRequest(
          api,
          'DELETE',
          `/api/files/${fileObjectId}?reason=fci-05-cleanup`,
        );
        assertSafeResponse(deleteResponse, { label: 'FCI-05 cleanup delete', expectedStatus: 200 });
        cleanupSucceeded = true;
        evidence.cleanupSucceeded = true;

        expect(await fileNamesForWorkspace(api, workspaceId)).not.toContain(fileName);

        const deletedRead = await api.get(`/api/files/${fileObjectId}`);
        assertSafeResponse(deletedRead, {
          label: 'FCI-05 deleted FileObject denial',
          expectedStatus: [400, 404],
        });
        const deletedReadPreview = await safeResponsePreview(deletedRead);
        expect(deletedReadPreview.includes(fileName), 'Deleted read omits file metadata').toBe(false);
        assertNoSensitiveText(deletedReadPreview);
        evidence.deletedReadStatus = deletedRead.status();

        const deletedGrant = await csrfAwareRequest(
          api,
          'POST',
          `/api/files/${fileObjectId}/download-grants`,
          { data: { purpose: 'fci-05-deleted-denial' } },
        );
        assertSafeResponse(deletedGrant, {
          label: 'FCI-05 deleted FileObject grant denial',
          expectedStatus: [400, 404],
        });
        const deletedGrantPreview = await safeResponsePreview(deletedGrant);
        expect(deletedGrantPreview.includes(fileName), 'Deleted grant denial omits file metadata').toBe(false);
        assertNoSensitiveText(deletedGrantPreview);
        evidence.deletedGrantStatus = deletedGrant.status();
        if (functionalFullExpansionEnabled()) {
          for (const suffix of ['activity', `versions/${fileObjectId}/content`]) {
            const denied = await api.get(`/api/files/${fileObjectId}/${suffix}`);
            assertSafeResponse(denied, { label: 'F05 deleted history/version denial', expectedStatus: [400, 404] });
          }
          evidence.deletedHistoryDenied = true;
        }
      } finally {
        // Recover only this run's object if response delivery/parsing failed.
        if (!cleanupSucceeded && uploadAttempted && workspaceId) {
          cleanupSucceeded = await cleanupUploadedFile(api, workspaceId, fileName, fileObjectId);
          evidence.cleanupSucceeded = cleanupSucceeded;
        }

        await testInfo.attach('fci-05-files-fast-evidence.json', {
          body: JSON.stringify(evidence, null, 2),
          contentType: 'application/json',
        });
      }
      if (uploadAttempted && !cleanupSucceeded) {
        throw new Error('FCI-05 could not verify run-owned FileObject cleanup; isolated storage teardown is still required.');
      }
    },
  );
});

async function resolveWorkspaceId(api: APIRequestContext, workspaceName: string): Promise<string> {
  const response = await api.get('/api/workspaces');
  assertSafeResponse(response, { label: 'FCI-05 Workspace list', expectedStatus: 200 });
  const body: unknown = await response.json();
  if (!Array.isArray(body)) {
    throw new Error('FCI-05 Workspace list was not an array.');
  }
  const workspace = body
    .map((item) => asRecord(item, 'Workspace list item'))
    .find((item) => readOptionalString(item, 'name', 'Name') === workspaceName);
  if (!workspace) {
    throw new Error(`FCI-05 seeded Workspace '${workspaceName}' was not found.`);
  }
  return requireStringField(workspace, 'id', 'Id');
}

async function readFileList(api: APIRequestContext, workspaceId: string): Promise<Record<string, unknown>[]> {
  const response = await api.get(`/api/files?workspaceId=${encodeURIComponent(workspaceId)}&page=1&pageSize=100`);
  assertSafeResponse(response, { label: 'FCI-05 File list', expectedStatus: 200 });
  const body = asRecord(await response.json(), 'File list response');
  const items = body.items ?? body.Items;
  if (!Array.isArray(items)) {
    throw new Error('FCI-05 File list response is missing items.');
  }
  return items.map((item) => asRecord(item, 'File list item'));
}

async function fileNamesForWorkspace(api: APIRequestContext, workspaceId: string): Promise<string[]> {
  const items = await readFileList(api, workspaceId);
  return items
    .map((item) => readOptionalString(item, 'originalFileName', 'OriginalFileName'))
    .filter((value): value is string => Boolean(value));
}

function asRecord(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} was not a JSON object.`);
  }
  return value as Record<string, unknown>;
}

function requireStringField(record: Record<string, unknown>, ...keys: string[]): string {
  const value = readOptionalString(record, ...keys);
  if (!value) {
    throw new Error(`Required string field is missing: ${keys.join(' / ')}`);
  }
  return value;
}

function readOptionalString(record: Record<string, unknown>, ...keys: string[]): string | null {
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'string' && value.length > 0) {
      return value;
    }
  }
  return null;
}

function assertNoStorageLeak(value: unknown): void {
  assertNoSensitiveText(JSON.stringify(value));
}

function assertNoSensitiveText(text: string): void {
  const normalized = text.toLowerCase();
  for (const forbidden of ['storagekey', 'storage_key', 'filepath', 'file_path', '/srv/', '/var/lib/', 'real_backend_smoke_uploads']) {
    if (normalized.includes(forbidden)) {
      throw new Error('FCI-05 response exposed an internal storage field or path. Response material is omitted.');
    }
  }
}

function assertSafeResponse(
  response: APIResponse | Response,
  options: { label: string; expectedStatus: number | number[] },
): void {
  const expected = Array.isArray(options.expectedStatus) ? options.expectedStatus : [options.expectedStatus];
  if (!expected.includes(response.status())) {
    // Grant/storage failure bodies may contain credentials or protected bytes.
    // Status and stable step labels are sufficient diagnostics for this owner.
    throw new Error(`${options.label}: HTTP ${response.status()}, expected ${expected.join(' or ')}. Response body omitted.`);
  }
}

async function cleanupUploadedFile(
  api: APIRequestContext, workspaceId: string, fileName: string, fileObjectId: string | null,
): Promise<boolean> {
  try {
    const candidates = (await readFileList(api, workspaceId)).filter((item) =>
      readOptionalString(item, 'originalFileName', 'OriginalFileName') === fileName,
    );
    if (candidates.length > 1) {
      return false;
    }
    const recoveredId = candidates.length === 1
      ? requireStringField(candidates[0], 'fileObjectId', 'FileObjectId')
      : null;
    if (fileObjectId && recoveredId && fileObjectId !== recoveredId) {
      return false;
    }
    const cleanupTargetId = fileObjectId ?? recoveredId;
    if (cleanupTargetId) {
      const cleanup = await csrfAwareRequest(
        api, 'DELETE', `/api/files/${cleanupTargetId}?reason=fci-05-finally-cleanup`,
      );
      if (cleanup.status() !== 200) {
        return false;
      }
    }
    return !(await fileNamesForWorkspace(api, workspaceId)).includes(fileName);
  } catch {
    // The isolated Compose project remains volume-cleaned by the outer harness.
    return false;
  }
}
