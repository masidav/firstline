import { type APIRequestContext, expect, test } from '@playwright/test';

import { isoToZoned } from '../src/lib/time';
import { expectAccessible } from './accessibility';
import { activeArtist, api, apiSession, organization, signIn, sql, uniqueName } from './fixtures';

const CHICAGO = 'America/Chicago';
const hoursFromNow = (hours: number) => isoToZoned(new Date(Date.now() + hours * 3_600_000).toISOString(), CHICAGO);

/** A published allocation on `eventId`, open for registration since an hour ago, with two fans registered. */
async function allocationWithFans(request: APIRequestContext, token: string, organizationId: string, eventId: string, artistId: string, name: string) {
  const allocation = await api<{ id: string }>(request, token, 'POST', `/organizations/${organizationId}/events/${eventId}/allocations`, {
    name, tickets: 1, face_value_cents: 4500, currency: 'USD', min_raw_score: 0,
    registration_opens_at: new Date(Date.now() - 3_600_000).toISOString(),
    registration_closes_at: new Date(Date.now() + 2 * 86_400_000).toISOString(),
    delivery_hours_before_show: 24,
  });
  await api(request, token, 'POST', `/organizations/${organizationId}/allocations/${allocation.id}/publish`);
  for (const score of [80, 60]) {
    const fan = await apiSession(request);
    await api(request, fan.token, 'PUT', '/me/hub-cities', { city_ids: ['us-il-chicago'] });
    await sql(
      `INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight, breakdown, computed_at)
       VALUES ($1, $2, $3, $3, false, 0.2, '{}', now())`,
      [fan.userId, artistId, score],
    );
    await api(request, fan.token, 'PUT', `/allocations/${allocation.id}/registration`, {});
  }
  return allocation.id;
}

/** Closes registration and runs the draw the way the worker would (the same database function). */
async function drawn(request: APIRequestContext, token: string, organizationId: string, allocationId: string) {
  await sql(`ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
             UPDATE allocations SET registration_closes_at = now() - interval '1 minute' WHERE id = '${allocationId}';
             ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;`);
  await api(request, token, 'POST', `/organizations/${organizationId}/allocations/${allocationId}/selection`);
  await sql('SELECT run_allocation_selection($1, NULL)', [allocationId]);
  await sql(`UPDATE jobs SET status = 'succeeded', finished_at = now() WHERE kind = 'allocations.select' AND payload->>'allocation_id' = $1`, [allocationId]);
}

// Every page the journeys visit, in the states they reach, scanned with axe (WCAG 2.2 AA and best practices;
// e2e/accessibility.ts). The data comes from the API, as the journeys make it, so the pages are full, not empty.
test('every dashboard page passes the axe scan', async ({ page, request }) => {
  const owner = await apiSession(request);
  const org = await organization(request, owner.handle);
  const emptyOrg = await organization(request, owner.handle);
  const artist = await activeArtist(request, owner.token);
  const { event } = await api<{ event: { id: string } }>(request, owner.token, 'POST', `/organizations/${org.id}/events`, {
    artist_id: artist.id, name: `${artist.name} at Metro`, venue: 'Metro', city_id: 'us-il-chicago', starts_at: '2027-03-21T01:00:00Z',
  });
  const open = await allocationWithFans(request, owner.token, org.id, event.id, artist.id, 'Fan allocation');
  const closed = await allocationWithFans(request, owner.token, org.id, event.id, artist.id, 'Second night');
  await drawn(request, owner.token, org.id, closed);
  const base = `/o/${org.id}`;

  await test.step('sign-in', async () => {
    await page.goto('/sign-in');
    await expect(page.getByLabel('Test account name')).toBeVisible();
    await page.getByLabel('Test account name').fill('nobody');
    await expectAccessible(page, 'sign-in');
  });

  await test.step('choosing a handle after Google sign-in', async () => {
    let state: string | null = null;
    await page.route('https://accounts.google.com/**', async (route) => {
      state = new URL(route.request().url()).searchParams.get('state');
      await route.fulfill({ status: 200, contentType: 'text/html', body: '<h1>Google (stub)</h1>' });
    });
    await page.getByRole('button', { name: 'Continue with Google' }).click();
    await page.waitForURL(/accounts\.google\.com/);
    const name = uniqueName('promo');
    await page.goto(`/google-callback#state=${state}&id_token=dev:${name}`);
    await expect(page.getByLabel('Handle')).toBeVisible();
    await expectAccessible(page, 'choose a handle');
    await page.unroute('https://accounts.google.com/**');

    // ...and the page that says to ask for an organization.
    await page.getByLabel('Handle').fill(name);
    await page.getByRole('button', { name: 'Create account' }).click();
    await expect(page.getByText("isn't part of a promoter organization yet", { exact: false })).toBeVisible();
    await expectAccessible(page, 'no organization yet');
    await page.getByRole('button', { name: 'Sign out' }).click();
  });

  await test.step('organizations', async () => {
    await signIn(page, owner.handle);
    await expect(page.getByRole('heading', { name: 'Your organizations' })).toBeVisible();
    await expect(page.getByRole('link', { name: org.name })).toBeVisible();
    await expectAccessible(page, 'organizations');
  });

  await test.step('shows', async () => {
    await page.goto(`/o/${emptyOrg.id}`);
    await expect(page.getByText('No shows yet. Add your first one.')).toBeVisible();
    await expectAccessible(page, 'shows (none yet)');

    await page.goto(base);
    await expect(page.getByRole('link', { name: `${artist.name} at Metro` })).toBeVisible();
    await expect(page.getByRole('cell', { name: artist.name, exact: true })).toBeVisible();
    await expectAccessible(page, 'shows');
  });

  await test.step('new show', async () => {
    await page.getByRole('link', { name: 'New show' }).click();
    await expect(page.getByRole('heading', { name: 'New show' })).toBeVisible();
    await page.getByLabel('Artist').fill(artist.name);
    await expect(page.getByRole('listitem').filter({ hasText: artist.name }).first()).toBeVisible();
    await expectAccessible(page, 'new show (search results open)');
    await page.getByRole('listitem').filter({ hasText: artist.name }).first().click();
    await expect(page.getByRole('button', { name: 'Change artist' })).toBeVisible();
    await expectAccessible(page, 'new show (artist chosen)');
  });

  await test.step('show', async () => {
    await page.goto(`${base}/shows/${event.id}`);
    await expect(page.getByRole('link', { name: 'Second night' })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Drawn', exact: true })).toBeVisible();
    await expectAccessible(page, 'show');
    await page.getByRole('button', { name: 'Edit show' }).click();
    await page.getByRole('button', { name: 'Add allocation' }).click();
    await expect(page.getByLabel('Show name')).toBeVisible();
    await expect(page.getByLabel('Registration opens')).toBeVisible();
    await expectAccessible(page, 'show (edit and allocation forms open)');
  });

  await test.step('allocation, registration open', async () => {
    await page.goto(`${base}/shows/${event.id}/allocations/${open}`);
    await expect(page.getByText('The draw can run once registration closes.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Export CSV' })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Registered', exact: true })).toHaveCount(2);
    await expectAccessible(page, 'allocation (registration open)');
  });

  await test.step('allocation, drawn, tickets held', async () => {
    await page.goto(`${base}/shows/${event.id}/allocations/${closed}`);
    await expect(page.getByText(/1 of 2 fans selected; 1 of 1 seats assigned/)).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Held', exact: true })).toBeVisible();
    await expectAccessible(page, 'allocation (drawn)');
  });

  await test.step('allocation, rules form open', async () => {
    const draft = await api<{ id: string }>(request, owner.token, 'POST', `/organizations/${org.id}/events/${event.id}/allocations`, {
      name: 'Draft night', tickets: 10, face_value_cents: 5000, currency: 'USD',
      registration_opens_at: new Date(Date.now() + 86_400_000).toISOString(), registration_closes_at: new Date(Date.now() + 2 * 86_400_000).toISOString(),
      delivery_hours_before_show: 24,
    });
    await page.goto(`${base}/shows/${event.id}/allocations/${draft.id}`);
    await page.getByRole('button', { name: 'Edit rules' }).click();
    await page.getByLabel('Registration opens').fill(hoursFromNow(24));
    await expect(page.getByRole('button', { name: 'Save rules' })).toBeVisible();
    await expectAccessible(page, 'allocation (rules form open)');
  });

  await test.step('members', async () => {
    await page.goto(`${base}/members`);
    await expect(page.getByRole('cell', { name: `@${owner.handle} · you` })).toBeVisible();
    await expect(page.getByLabel('Add a member by handle')).toBeVisible();
    await expectAccessible(page, 'members (owner)');
  });
});
