/* eslint-disable max-lines-per-function -- Keep the real Files lifecycle steps and fresh-read assertions together. */
import { expect, test, type APIRequestContext, type APIResponse, type Locator, type Page, type Response } from '@playwright/test';

import { csrfAwareRequest } from '../helpers/csrf';

interface FilesLifecycleContext {
  api: APIRequestContext;
  page: Page;
  workspaceId: string;
  fileObjectId: string;
  inventoryRowId: string;
  fileName: string;
  content: Buffer;
}

/** Use the existing adapter row hooks; inventory rows carry Attachment IDs. */
export function fileRowAction(page: Page, rowId: string): Locator {
  return page.getByTestId('files-page').locator(
    `[data-grid-row-id="${rowId}"][data-grid-action="open"], [row-id="${rowId}"] [data-grid-action="open"]`,
  );
}

/** The full expansion uses the same upload and identity as the bounded owner. */
export async function runFilesLifecycle(context: FilesLifecycleContext): Promise<Record<string, unknown>> {
  const { api, page, workspaceId, fileObjectId, inventoryRowId, fileName, content } = context;
  const evidence: Record<string, unknown> = {};
  const inspector = page.getByTestId('files-preview-pane');
  const openFile = fileRowAction(page, inventoryRowId);

  await test.step('FUNC-FILE-002 / F05-FULL-01 / search and reopen', async () => {
    await page.getByTestId('files-search-input').fill(fileName);
    const searchResponse = page.waitForResponse((response) => {
      const url = new URL(response.url());
      return response.request().method() === 'GET' && url.pathname === '/api/search' &&
        url.searchParams.get('type') === 'File' && url.searchParams.get('q') === fileName;
    });
    await page.getByTestId('files-search-submit').click();
    const searchResult = await searchResponse;
    requireStatus(searchResult, 200, 'F05-FULL-01 search');
    const results = recordArray(record(await searchResult.json()).items);
    const matches = results.filter((item) => item.id === fileObjectId && item.type === 'File');
    expect(matches, 'Real search returns the same logical FileObject').toHaveLength(1);
    const [searchMatch] = matches;
    expect(searchMatch.workspaceId).toBe(workspaceId);
    expect(searchMatch.title).toBe(fileName);
    // SearchSnippet projects title; canonical inventory/FileMetadata redacts it.
    const searchAction = fileRowAction(page, fileObjectId);
    await expect(searchAction).toBeVisible();
    await expect(searchAction).toHaveAccessibleName(fileName);
    await searchAction.click();
    await expect(inspector.getByRole('heading', { name: fileName, exact: true })).toBeVisible();
    await page.getByTestId('files-search-clear').click();
    evidence.searchSucceeded = true;
  });

  await test.step('FUNC-FILE-002 / F05-FULL-02 / move, reload, and reject a stale mutation', async () => {
    const folderName = `fci05-folder-${fileObjectId}`;
    const created = await csrfAwareRequest(api, 'POST', '/api/file-folders', {
      data: { workspaceId, parentFolderId: null, name: folderName },
    });
    requireStatus(created, 200, 'F05-FULL-02 create destination');
    const createdBody = record(await created.json());
    const folderId = stringField(createdBody, 'id');
    const navigation = await readJson(api, `/api/file-folders?workspaceId=${workspaceId}`);
    const folders = recordArray(navigation.folders);
    const destination = folders.find((folder) => folder.id === folderId);
    expect(destination?.name === folderName && destination?.workspaceId === workspaceId,
      'Fresh folder navigation contains the run-owned destination').toBe(true);
    const beforeMove = await readJson(api, `/api/files/${fileObjectId}/location`);
    expect(beforeMove.folderId).toBeNull();

    await page.reload();
    await expect(openFile).toBeVisible();
    await expect(openFile).toHaveAccessibleName('[redacted:file]');
    const row = openFile.locator('xpath=ancestor::*[@role="row"][1]');
    await row.getByRole('checkbox').check();
    await page.getByTestId('files-selected-move').click();
    await page.getByTestId('files-move-destination').selectOption(folderId);
    const moveResponse = page.waitForResponse((response) =>
      response.request().method() === 'POST' && new URL(response.url()).pathname === `/api/files/${fileObjectId}/move`,
    );
    await page.getByRole('dialog', { name: 'Move', exact: true }).getByRole('button', { name: 'Move', exact: true }).click();
    requireStatus(await moveResponse, 200, 'F05-FULL-02 UI move');
    const persistedLocation = await readJson(api, `/api/files/${fileObjectId}/location`);
    expect(persistedLocation.fileObjectId).toBe(fileObjectId);
    expect(persistedLocation.folderId).toBe(folderId);
    expect(numberField(persistedLocation, 'version')).toBe(numberField(beforeMove, 'version') + 1);

    const staleMove = await csrfAwareRequest(api, 'POST', `/api/files/${fileObjectId}/move`, {
      data: {
        destinationFolderId: null, expectedVersion: beforeMove.version,
        expectedDestinationVersion: navigation.rootVersion,
      },
    });
    requireStatus(staleMove, 400, 'F05-FULL-02 stale move');
    const afterRejection = await readJson(api, `/api/files/${fileObjectId}/location`);
    expect(afterRejection.folderId).toBe(folderId);
    expect(afterRejection.version).toBe(persistedLocation.version);

    await page.reload();
    // Folder shortcuts currently render an unavailable/empty product surface.
    // Verify the real move and persisted navigation metadata through Recent.
    await expect(page.getByRole('button', { name: folderName, exact: true })).toBeVisible();
    await expect(openFile).toBeVisible();
    const reloadLocation = await readJson(api, `/api/files/${fileObjectId}/location`);
    expect(reloadLocation.folderId).toBe(folderId);
    expect(reloadLocation.version).toBe(persistedLocation.version);
    evidence.movePersisted = true;
    evidence.staleMoveRejected = true;
  });

  await test.step('FUNC-FILE-002 / F05-FULL-03 / sharing transition and fresh authority', async () => {
    await openFile.click();
    const initial = await readJson(api, `/api/files/${fileObjectId}/sharing`);
    expect(initial.canManageSharing).toBe(true);
    expect(initial.accessState).toBe('Private');
    await inspector.getByTestId('files-preview-manage-sharing').click();
    const toggle = page.getByTestId('files-sharing-workspace-toggle');
    await expect(toggle).not.toBeChecked();
    const sharingResponse = page.waitForResponse((response) =>
      response.request().method() === 'PUT' && new URL(response.url()).pathname === `/api/files/${fileObjectId}/sharing`,
    );
    await toggle.check();
    requireStatus(await sharingResponse, 200, 'F05-FULL-03 UI sharing change');
    const shared = await readJson(api, `/api/files/${fileObjectId}/sharing`);
    expect(shared.fileObjectId).toBe(fileObjectId);
    expect(shared.accessState).toBe('Workspace');
    expect(shared.sharingPolicy).toBe('Workspace');
    expect(numberField(shared, 'sharingVersion')).toBe(numberField(initial, 'sharingVersion') + 1);
    await expect(inspector.getByTestId('files-preview-access-state')).toHaveText('Workspace');

    const staleSharing = await csrfAwareRequest(api, 'PUT', `/api/files/${fileObjectId}/sharing`, {
      data: { shareWithWorkspace: false, expectedSharingVersion: initial.sharingVersion },
    });
    requireStatus(staleSharing, 400, 'F05-FULL-03 stale sharing update');
    const afterRejection = await readJson(api, `/api/files/${fileObjectId}/sharing`);
    expect(afterRejection.accessState).toBe('Workspace');
    expect(afterRejection.sharingVersion).toBe(shared.sharingVersion);

    await page.reload();
    await expect(openFile).toBeVisible();
    await openFile.click();
    await expect(inspector.getByTestId('files-preview-access-state')).toHaveText('Workspace');
    const reloaded = await readJson(api, `/api/files/${fileObjectId}/sharing`);
    expect(reloaded.sharingVersion).toBe(shared.sharingVersion);
    expect(reloaded.accessState).toBe('Workspace');
    const detail = await readJson(api, `/api/files/${fileObjectId}`);
    expect(detail.id).toBe(fileObjectId);
    expect(detail.workspaceId).toBe(workspaceId);
    expect(detail.originalFileName).toBe('[redacted:file]');
    expect(detail.accessState).toBe('Workspace');
    expect(detail.sharingVersion).toBe(shared.sharingVersion);
    const list = await readJson(api, `/api/files?workspaceId=${workspaceId}&page=1&pageSize=100`);
    const matches = recordArray(list.items).filter((item) => item.fileObjectId === fileObjectId);
    expect(matches).toHaveLength(1);
    const [listItem] = matches;
    expect(listItem.id).toBe(inventoryRowId);
    expect(listItem.workspaceId).toBe(workspaceId);
    expect(listItem.originalFileName).toBe('[redacted:file]');
    expect(listItem.accessState).toBe('Workspace');
    expect(listItem.sharingVersion).toBe(shared.sharingVersion);
    evidence.sharingPersisted = true;
    evidence.staleSharingRejected = true;
  });

  await test.step('FUNC-FILE-002 / F05-FULL-04 / version and sharing history remain coherent', async () => {
    const activity = await readJson(api, `/api/files/${fileObjectId}/activity`);
    expect(activity.fileObjectId).toBe(fileObjectId);
    const items = recordArray(activity.items);
    const versions = items.filter((item) => item.kind === 'uploaded' || item.kind === 'versionCreated');
    expect(versions).toHaveLength(1);
    const version = record(versions[0].version);
    expect(version.versionNumber).toBe(1);
    expect(version.isCurrent).toBe(true);
    expect(version.fileName).toBe('[redacted:file]');
    expect(version.contentType).toBe('text/plain');
    expect(version.sizeBytes).toBe(content.byteLength);
    // The current product's migration captures version 1 with this exact ID.
    expect(version.versionId).toBe(fileObjectId);
    const sharingEvents = items.filter((item) => item.kind === 'sharingChanged');
    expect(sharingEvents.length).toBeGreaterThan(0);
    expect(sharingEvents.some((item) => record(item.sharing).accessState === 'workspace')).toBe(true);
    const versionRead = await api.get(`/api/files/${fileObjectId}/versions/${fileObjectId}/content`);
    requireStatus(versionRead, 200, 'F05-FULL-04 version content');
    expect((await versionRead.body()).equals(content), 'Version bytes match the uploaded fixture').toBe(true);

    const activityResponse = page.waitForResponse((response) =>
      response.request().method() === 'GET' && new URL(response.url()).pathname === `/api/files/${fileObjectId}/activity`,
    );
    await inspector.getByTestId('files-inspector-tab-activity').click();
    requireStatus(await activityResponse, 200, 'F05-FULL-04 UI history');
    await expect(inspector.getByTestId('files-activity-version-entry')).toContainText('Version 1');
    await inspector.getByTestId('files-activity-view-version').click();
    await expect.poll(async () => {
      const text = await inspector.getByTestId('files-version-preview').textContent();
      return text?.includes(content.toString('utf8').trim()) ?? false;
    }, { message: 'Historical UI preview matches synthetic fixture bytes' }).toBe(true);

    await page.reload();
    const reloaded = await readJson(api, `/api/files/${fileObjectId}/activity`);
    const current = recordArray(reloaded.items).filter((item) => item.version && record(item.version).isCurrent);
    expect(current).toHaveLength(1);
    expect(record(current[0].version).versionId).toBe(fileObjectId);
    evidence.versionPersisted = true;
    evidence.historyPersisted = true;
  });
  return evidence;
}

function requireStatus(response: APIResponse | Response, status: number, step: string): void {
  if (response.status() !== status) {
    throw new Error(`${step}: HTTP ${response.status()}, expected ${status}. Response body omitted.`);
  }
}

async function readJson(api: APIRequestContext, path: string): Promise<Record<string, unknown>> {
  const response = await api.get(path);
  requireStatus(response, 200, 'F05 fresh persisted read');
  return record(await response.json());
}

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('F05 expected an object; response material omitted.');
  }
  return value as Record<string, unknown>;
}

function recordArray(value: unknown): Record<string, unknown>[] {
  if (!Array.isArray(value)) {
    throw new Error('F05 expected an inventory array; response material omitted.');
  }
  return value.map(record);
}

function stringField(value: Record<string, unknown>, key: string): string {
  if (typeof value[key] !== 'string' || !value[key]) {
    throw new Error(`F05 missing ${key}; response material omitted.`);
  }
  return value[key];
}

function numberField(value: Record<string, unknown>, key: string): number {
  if (typeof value[key] !== 'number' || !Number.isSafeInteger(value[key]) || value[key] < 0) {
    throw new Error(`F05 invalid ${key}; response material omitted.`);
  }
  return value[key];
}
