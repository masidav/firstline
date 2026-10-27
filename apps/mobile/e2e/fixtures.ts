import { type APIRequestContext, expect, type Page } from '@playwright/test';
import { Client } from 'pg';

import { settings } from './settings';

/** A fresh account name: tests never share accounts, so they run in parallel and on a database used before. */
export function uniqueName(prefix: string): string {
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 36 ** 4).toString(36)}`;
  return `${prefix}_${suffix}`.slice(0, 30);
}

/** Runs a query against the e2e database (for what only a background job would do, like computing a score). */
export async function sql<T extends Record<string, unknown>>(text: string, values: unknown[] = []): Promise<T[]> {
  const client = new Client({ connectionString: settings.databaseUrl });
  await client.connect();
  try {
    return (await client.query<T>(text, values)).rows;
  } finally {
    await client.end();
  }
}

/** Signs a new fan up through the app: developer sign-in, a handle, a hub city, then home. */
export async function signUp(page: Page, name = uniqueName('fan'), city = 'Chicago'): Promise<string> {
  await page.goto('/sign-in');
  await page.getByLabel('Test account name').fill(name);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await chooseHandle(page, name);
  await chooseHubCity(page, city);
  return name;
}

export async function chooseHandle(page: Page, handle: string): Promise<void> {
  const field = page.getByLabel('Handle');
  await expect(field).toBeVisible();
  await field.fill(handle);
  await page.getByRole('button', { name: 'Create account' }).click();
}

export async function chooseHubCity(page: Page, city: string): Promise<void> {
  await page.waitForURL(/\/hub-cities/);
  await page.getByLabel('Search for your city').fill(city);
  await page.getByRole('button', { name: new RegExp(`^Add ${city}`) }).first().click();
  await page.getByRole('button', { name: 'Continue' }).click();
  await page.waitForURL(/\/home/);
  await expect(page.getByRole('heading', { name: 'Your artists' })).toBeVisible();
}

/** A signed-in API session for a new account, made staff when asked (staff are made by hand in production too). */
export async function apiSession(
  request: APIRequestContext,
  staff = false,
): Promise<{ token: string; userId: string; handle: string }> {
  const handle = uniqueName(staff ? 'staff' : 'apifan');
  const response = await request.post(`${settings.apiUrl}/auth/sessions`, {
    data: { provider: 'google', id_token: `dev:${handle}`, handle },
  });
  expect(response.status(), await response.text()).toBe(201);
  const body = (await response.json()) as { access_token: string; user: { id: string } };
  if (staff) {
    await sql('UPDATE users SET is_staff = true WHERE id = $1', [body.user.id]);
  }
  return { token: body.access_token, userId: body.user.id, handle };
}

/** Calls the API as the given session; fails the test on an error status. */
export async function api<T>(
  request: APIRequestContext,
  token: string,
  method: 'GET' | 'POST',
  path: string,
  data?: unknown,
): Promise<T> {
  const response = await request.fetch(`${settings.apiUrl}${path}`, {
    method,
    data,
    headers: { Authorization: `Bearer ${token}` },
  });
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBe(true);
  return (await response.json()) as T;
}

/** An active artist from the seed data that has albums. */
export async function artistWithAlbums(request: APIRequestContext, token: string) {
  const artists = await api<{ items: { id: string; name: string }[] }>(
    request, token, 'GET', '/artists?status=active&sort=name&limit=50');
  for (const artist of artists.items) {
    const albums = await api<{ items: { id: string; title: string }[] }>(
      request, token, 'GET', `/artists/${artist.id}/albums?limit=5`);
    if (albums.items.length > 0) {
      return { artist, album: albums.items[0]! };
    }
  }
  throw new Error('No active artist with albums: load the seed data (tools/dev/seed.py --fake).');
}
