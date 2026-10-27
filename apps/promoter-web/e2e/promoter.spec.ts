import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';

import { isoToZoned } from '../src/lib/time';
import { activeArtist, api, apiSession, organization, signIn, sql, uniqueName } from './fixtures';

const CHICAGO = 'America/Chicago';

test('a promoter sets up a show and allocation, watches fans register, runs the draw, and exports the results', async ({ page, request }) => {
  const owner = await apiSession(request);
  const org = await organization(request, owner.handle);
  const artist = await activeArtist(request, owner.token);

  // With one organization, signing in goes straight to its shows.
  await signIn(page, owner.handle);
  await page.waitForURL(new RegExp(`/o/${org.id}$`));
  await expect(page.getByRole('heading', { name: 'Shows' })).toBeVisible();
  await expect(page.getByText('No shows yet. Add your first one.')).toBeVisible();

  // A show in Chicago at 8 PM Chicago time (daylight time in March).
  await page.getByRole('link', { name: 'New show' }).click();
  await page.getByLabel('Artist').fill(artist.name);
  await page.getByRole('listitem').filter({ hasText: artist.name }).first().click();
  await page.getByLabel('City').fill('Chicago');
  await page.getByRole('listitem').filter({ hasText: 'Chicago, IL' }).click();
  await page.getByLabel('Venue').fill('Metro');
  await page.getByLabel('Starts').fill('2027-03-20T20:00');
  await page.getByRole('button', { name: 'Create show' }).click();
  await page.waitForURL(/\/shows\/[0-9a-f-]+$/);
  const eventId = page.url().split('/').pop()!;
  await expect(page.getByText(/Mar 20, 2027.*8:00\s?PM CDT/)).toBeVisible();
  const [stored] = await sql<{ starts_at: Date }>('SELECT starts_at FROM events WHERE id = $1', [eventId]);
  expect(stored!.starts_at.toISOString()).toBe('2027-03-21T01:00:00.000Z');

  // One ticket, registration open now.
  await page.getByRole('button', { name: 'Add allocation' }).click();
  await page.getByLabel('Tickets', { exact: true }).fill('1');
  await page.getByLabel('Face value').fill('45');
  await page.getByLabel('Minimum raw score').fill('0');
  await page.getByLabel('Registration opens').fill(isoToZoned(new Date(Date.now() - 60 * 60_000).toISOString(), CHICAGO));
  await page.getByLabel('Registration closes').fill(isoToZoned(new Date(Date.now() + 2 * 86_400_000).toISOString(), CHICAGO));
  await page.getByRole('button', { name: 'Create draft' }).click();
  await expect(page.getByRole('cell', { name: 'Draft', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Publish' }).click();
  await expect(page.getByRole('cell', { name: 'Registration open', exact: true })).toBeVisible();
  const allocationId = (await sql<{ id: string }>('SELECT id FROM allocations WHERE event_id = $1', [eventId]))[0]!.id;

  // Two fans from Chicago register in the fan app (through the API here); the higher score gets the ticket.
  const fans = [];
  for (const score of [80, 60]) {
    const fan = await apiSession(request);
    await api(request, fan.token, 'PUT', '/me/hub-cities', { city_ids: ['us-il-chicago'] });
    await sql(
      `INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight, breakdown, computed_at)
       VALUES ($1, $2, $3, $3, false, 0.2, '{}', now())`,
      [fan.userId, artist.id, score],
    );
    await api(request, fan.token, 'PUT', `/allocations/${allocationId}/registration`, {});
    fans.push(fan.handle);
  }

  await page.getByRole('link', { name: 'Fan allocation' }).click();
  await expect(page.getByText(`@${fans[0]}`)).toBeVisible();
  await expect(page.getByText(`@${fans[1]}`)).toBeVisible();
  await expect(page.getByText('The draw can run once registration closes.')).toBeVisible();

  // Registration closes (the clock, here), the promoter runs the draw, and the worker does it (the same function here).
  await sql(`ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
             UPDATE allocations SET registration_closes_at = now() - interval '1 minute' WHERE id = '${allocationId}';
             ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;`);
  await page.reload();
  await page.getByRole('button', { name: 'Run the draw' }).click();
  await expect(page.getByText(/The draw is queued/)).toBeVisible();
  await sql('SELECT run_allocation_selection($1, NULL)', [allocationId]);
  await sql(`UPDATE jobs SET status = 'succeeded', finished_at = now() WHERE kind = 'allocations.select' AND payload->>'allocation_id' = $1`, [allocationId]);

  await expect(page.getByText(/1 of 2 fans selected; 1 of 1 seats assigned/)).toBeVisible({ timeout: 20_000 });
  const rows = page.getByRole('row');
  await expect(rows.filter({ hasText: `@${fans[0]}` })).toContainText('Selected');
  await expect(rows.filter({ hasText: `@${fans[1]}` })).toContainText('Not selected');
  await expect(rows.filter({ hasText: `@${fans[0]}` })).toContainText('Held');

  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export CSV' }).click();
  const csv = await readFile((await (await download).path())!, 'utf8');
  expect(csv).toContain(`${fans[0]},selected,true,false,1,1,`);
  expect(csv).toContain(`${fans[1]},not_selected,true,false,2,,`);
});

test('an analyst can look at everything but change nothing, and another organization stays hidden', async ({ page, request }) => {
  const owner = await apiSession(request);
  const analyst = await apiSession(request);
  const org = await organization(request, owner.handle);
  await api(request, owner.token, 'PUT', `/organizations/${org.id}/members/${analyst.handle}`, { role: 'analyst' });
  const other = await organization(request, (await apiSession(request)).handle);

  await signIn(page, analyst.handle);
  await page.waitForURL(new RegExp(`/o/${org.id}$`));
  await expect(page.getByText('No shows yet.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'New show' })).toHaveCount(0);

  await page.getByRole('link', { name: 'Members' }).click();
  await expect(page.getByText(`@${owner.handle}`)).toBeVisible();
  await expect(page.getByLabel('Add a member by handle')).toHaveCount(0);

  await page.goto(`/o/${other.id}`);
  await expect(page.getByText('No organization has this id.')).toBeVisible();
});

test('Google sign-in brings a new promoter back to choose a handle, then says to ask for an organization', async ({ page }) => {
  let state: string | null = null;
  await page.route('https://accounts.google.com/**', async (route) => {
    state = new URL(route.request().url()).searchParams.get('state');
    await route.fulfill({ status: 200, contentType: 'text/html', body: '<h1>Google (stub)</h1>' });
  });

  await page.goto('/sign-in');
  await page.getByRole('button', { name: 'Continue with Google' }).click();
  await page.waitForURL(/accounts\.google\.com/);
  const name = uniqueName('promo');
  await page.goto(`/google-callback#state=${state}&id_token=dev:${name}`);

  await page.getByLabel('Handle').fill(name);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByText(`Your account (@${name}) isn't part of a promoter organization yet.`, { exact: false })).toBeVisible();
});
