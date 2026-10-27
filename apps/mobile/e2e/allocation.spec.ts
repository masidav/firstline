import { expect, test } from '@playwright/test';

import { api, apiSession, artistWithAlbums, signUp, sql, uniqueName } from './fixtures';

test('an eligible fan registers for a fan allocation, stays registered after a reload, and can withdraw', async ({ page, request }) => {
  // Staff set up a show in Chicago with an allocation that is open now (through the admin API, as staff do).
  const staff = await apiSession(request, true);
  const { artist } = await artistWithAlbums(request, staff.token);
  const day = 24 * 60 * 60 * 1000;
  const show = await api<{ event: { id: string } }>(request, staff.token, 'POST', '/admin/events', {
    artist_id: artist.id,
    name: `E2E show ${uniqueName('s')}`,
    venue: 'Metro',
    city_id: 'us-il-chicago',
    starts_at: new Date(Date.now() + 30 * day).toISOString(),
  });
  const allocation = await api<{ id: string }>(request, staff.token, 'POST', `/admin/events/${show.event.id}/allocations`, {
    tickets: 50,
    face_value_cents: 4500,
    min_raw_score: 0,
    registration_opens_at: new Date(Date.now() - 60_000).toISOString(),
    registration_closes_at: new Date(Date.now() + 7 * day).toISOString(),
  });
  await api(request, staff.token, 'POST', `/admin/allocations/${allocation.id}/publish`);

  // A fan from Chicago with a fandom score for the artist (normally the scoring job writes it after a Spotify sync).
  const handle = await signUp(page, uniqueName('ticket'), 'Chicago');
  await sql(
    `INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight, breakdown, computed_at)
     SELECT id, $1, 50, 90, false, 0.2, '{}', now() FROM users WHERE handle = $2`,
    [artist.id, handle],
  );

  await page.goto(`/show/${show.event.id}`);
  await page.getByRole('button', { name: 'Register' }).click();
  await expect(page.getByText("You're registered.")).toBeVisible();

  await page.reload();
  await expect(page.getByText("You're registered.")).toBeVisible();

  await page.getByRole('button', { name: 'Withdraw' }).click();
  await expect(page.getByRole('button', { name: 'Register' })).toBeVisible();
  await expect(page.getByText("You're registered.")).toHaveCount(0);
});
