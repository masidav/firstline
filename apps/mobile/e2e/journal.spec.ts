import { expect, type Page, test } from '@playwright/test';

import { apiSession, artistWithAlbums, signUp, sql } from './fixtures';

/** From home: search the artist, open their albums, open the album. */
async function openAlbum(page: Page, artistName: string, albumTitle: string) {
  await page.getByLabel('Search artists').filter({ visible: true }).fill(artistName);
  await page.getByRole('button', { name: `Open ${artistName}` }).filter({ visible: true }).first().click();
  await page.waitForURL(/\/artist\/.+\/albums/);
  await page.getByRole('button', { name: `Open ${albumTitle}` }).filter({ visible: true }).first().click();
  await page.waitForURL(/\/album\//);
  await expect(page.getByRole('button', { name: 'Log this album' })).toBeVisible();
}

test('a fan logs an album with a rating and review, earns points, and finds it in the journal', async ({ page, request }) => {
  const { artist, album } = await artistWithAlbums(request, (await apiSession(request)).token);
  await signUp(page);
  await openAlbum(page, artist.name, album.title);

  await page.getByRole('button', { name: 'Log this album' }).click();
  await page.getByLabel('Rate 4 stars').click();
  await page.getByLabel('Review').fill('Logged by the end-to-end suite.');
  await page.getByRole('button', { name: 'Save log' }).click();
  await expect(page.getByText(/^You earned .* points/)).toBeVisible();

  await page.goto('/journal');
  await expect(page.getByText('Logged by the end-to-end suite.')).toBeVisible();
  await expect(page.getByRole('button', { name: `Delete log of ${album.title}` })).toHaveCount(1);
});

test('a log saved without a connection syncs once the connection is back, and only once', async ({ page, context, request }) => {
  const { artist, album } = await artistWithAlbums(request, (await apiSession(request)).token);
  const handle = await signUp(page);
  await openAlbum(page, artist.name, album.title);

  await context.setOffline(true);
  await page.getByRole('button', { name: 'Log this album' }).click();
  await page.getByLabel('Rate 5 stars').click();
  await page.getByLabel('Review').fill('Logged on the train.');
  await page.getByRole('button', { name: 'Save log' }).click();
  await expect(page.getByText("Saved on this device. It will sync when you're back online.")).toBeVisible();

  await context.setOffline(false);
  await expect(page.getByText(/Saved on this device/)).toHaveCount(0, { timeout: 30_000 });
  await expect(page.getByText(/^You earned .* points/)).toBeVisible();

  // The form's attempt and the queue's retries all carried one Idempotency-Key: one log on the server.
  const logs = await sql<{ count: string }>(
    `SELECT count(*) FROM album_logs l JOIN users u ON u.id = l.user_id
     WHERE u.handle = $1 AND l.album_id = $2 AND l.deleted_at IS NULL`,
    [handle, album.id],
  );
  expect(logs[0]?.count).toBe('1');
});
