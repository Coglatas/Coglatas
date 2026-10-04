import { expect, type APIRequestContext, type Page, type Response } from '@playwright/test';

import { csrfAwareRequest } from './csrf';
import { assertSafeResponse } from './safe-response';

export interface FunctionalCredentials {
  email: string;
  password: string;
}

export async function loginViaApi(api: APIRequestContext, credentials: FunctionalCredentials): Promise<Record<string, unknown>> {
  const response = await csrfAwareRequest(api, 'POST', '/api/auth/login', {
    data: { email: credentials.email, password: credentials.password }
  });
  await assertSafeResponse(response, { label: 'Functional API login', expectedStatus: 200 });
  return (await response.json()) as Record<string, unknown>;
}

export async function logoutViaApi(api: APIRequestContext): Promise<void> {
  const response = await csrfAwareRequest(api, 'POST', '/api/auth/logout', { data: {} });
  await assertSafeResponse(response, { label: 'Functional API logout', expectedStatus: 200 });
}

export async function readCurrentSession(api: APIRequestContext): Promise<Record<string, unknown>> {
  const response = await api.get('/api/auth/me');
  await assertSafeResponse(response, { label: 'Functional current session', expectedStatus: 200 });
  return (await response.json()) as Record<string, unknown>;
}

export async function loginViaUi(page: Page, credentials: FunctionalCredentials): Promise<Response> {
  const document = await page.goto('/app/login');
  expect(document?.status()).toBe(200);
  await expect(page.getByTestId('login-page')).toBeVisible();
  await page.getByTestId('login-email').fill(credentials.email);
  await page.getByTestId('login-password').fill(credentials.password);
  const loginPromise = page.waitForResponse((response) =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/auth/login',
  );
  await page.getByTestId('login-submit').click();
  const login = await loginPromise;
  expect(login.status()).toBe(200);
  await expect(page).toHaveURL(/\/app\/workspaces(?:[/?#]|$)/u);
  await expect(page.getByTestId('app-shell')).toBeVisible();
  return login;
}

export async function expectLoggedOut(page: Page): Promise<void> {
  await page.goto('/app/workspaces');
  await expect(page).toHaveURL(/\/app\/login(?:[/?#]|$)/u);
  await expect(page.getByTestId('login-page')).toBeVisible();
}
