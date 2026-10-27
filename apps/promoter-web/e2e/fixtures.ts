import { type APIRequestContext, expect, type Page } from '@playwright/test';
import pg from 'pg';

import { settings } from './settings';

export function uniqueName(prefix: string): string {
  return `${prefix}_${Date.now().toString(36)}${Math.floor(Math.random() * 36 ** 4).toString(36)}`.slice(0, 30);
}

export async function sql<T extends Record<string, unknown>>(text: string, values: unknown[] = []): Promise<T[]> {
  const client = new pg.Client({ connectionString: settings.databaseUrl });
  await client.connect();
  try {
    return (await client.query<T>(text, values)).rows;
  } finally {
    await client.end();
  }
}

/** A new account through the API (developer sign-in), made staff when asked. */
export async function apiSession(request: APIRequestContext, staff = false) {
  const handle = uniqueName(staff ? 'staff' : 'user');
  const response = await request.post(`${settings.apiUrl}/auth/sessions`, { data: { provider: 'google', id_token: `dev:${handle}`, handle } });
  expect(response.status(), await response.text()).toBe(201);
  const body = (await response.json()) as { access_token: string; user: { id: string } };
  if (staff) await sql('UPDATE users SET is_staff = true WHERE id = $1', [body.user.id]);
  return { token: body.access_token, userId: body.user.id, handle };
}

export async function api<T>(request: APIRequestContext, token: string, method: 'GET' | 'POST' | 'PUT', path: string, data?: unknown): Promise<T> {
  const response = await request.fetch(`${settings.apiUrl}${path}`, { method, data, headers: { Authorization: `Bearer ${token}` } });
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

/** An active artist from the seed data. */
export async function activeArtist(request: APIRequestContext, token: string) {
  const artists = await api<{ items: { id: string; name: string }[] }>(request, token, 'GET', '/artists?status=active&sort=name&limit=1');
  if (!artists.items[0]) throw new Error('No active artist: load the seed data (tools/dev/seed.py --fake).');
  return artists.items[0];
}

/** A promoter organization staff created, with `owner` as its first owner. */
export async function organization(request: APIRequestContext, owner: string) {
  const staff = await apiSession(request, true);
  return api<{ id: string; name: string }>(request, staff.token, 'POST', '/admin/organizations', { name: `E2E ${uniqueName('org')}`, owner_handle: owner });
}

/** Signs an existing account in through the dashboard's developer sign-in. */
export async function signIn(page: Page, handle: string) {
  await page.goto('/sign-in');
  await page.getByLabel('Test account name').fill(handle);
  await page.getByRole('button', { name: 'Sign in' }).click();
}
