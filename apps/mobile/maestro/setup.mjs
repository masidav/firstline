// Prepares the data the Maestro journeys need (maestro/README.md), the way the browser suite's fixtures do
// (e2e/fixtures.ts): accounts through the API's developer sign-in, a show and an open allocation through the admin
// API, and what only a background job would write (a fandom score) straight into the database. Prints the values
// as KEY=value lines, which run.sh passes to Maestro with `-e`.
//
//   node maestro/setup.mjs            # API at http://127.0.0.1:5271/v1, database from E2E_DATABASE_URL
//
// Every run uses new account names, so the flows can run again on the same database.
import { randomBytes } from 'node:crypto';

import pg from 'pg';

const apiUrl = process.env.MAESTRO_API_URL ?? 'http://127.0.0.1:5271/v1';
const databaseUrl = process.env.E2E_DATABASE_URL ?? 'postgres://fandom:e2e@127.0.0.1:55480/fandom_e2e';
const run = `${Date.now().toString(36).slice(-5)}${randomBytes(2).toString('hex')}`;
const day = 24 * 60 * 60 * 1000;

async function call(token, method, path, body) {
  const response = await fetch(`${apiUrl}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text}`);
  return text ? JSON.parse(text) : undefined;
}

async function sql(text, values = []) {
  const client = new pg.Client({ connectionString: databaseUrl });
  await client.connect();
  try {
    return (await client.query(text, values)).rows;
  } finally {
    await client.end();
  }
}

/** A fan whose developer sign-in name is also their handle, with Chicago as hub city (so sign-in lands on home). */
async function fan(prefix, { hubCity = true } = {}) {
  const handle = `${prefix}_${run}`;
  const session = await call(null, 'POST', '/auth/sessions', {
    provider: 'google',
    id_token: `dev:${handle}`,
    nonce: 'maestro-nonce',
    handle,
  });
  if (hubCity) await call(session.access_token, 'PUT', '/me/hub-cities', { city_ids: ['us-il-chicago'] });
  return { handle, token: session.access_token, id: session.user.id };
}

const staff = await fan('mstaff');
await sql('UPDATE users SET is_staff = true WHERE id = $1', [staff.id]);

// An active seeded artist with albums (tools/dev/seed.py --fake).
const artists = await call(staff.token, 'GET', '/artists?status=active&sort=name&limit=50');
let artist;
let album;
for (const candidate of artists.items) {
  const albums = await call(staff.token, 'GET', `/artists/${candidate.id}/albums?limit=5`);
  if (albums.items.length > 0) {
    artist = candidate;
    album = albums.items[0];
    break;
  }
}
if (!artist) throw new Error('No active artist with albums: load the seed data (tools/dev/seed.py --fake).');

// Home: a signed-up fan; the seed's public logs and prompt answers fill the feed (it is shuffled, so the flow checks
// that cards arrive, not which).
const home = await fan('mhome');

// Allocation: a show in Chicago with an allocation open now, and a Chicago fan with a score for the artist.
const show = await call(staff.token, 'POST', '/admin/events', {
  artist_id: artist.id,
  name: `Maestro show ${run}`,
  venue: 'Metro',
  city_id: 'us-il-chicago',
  starts_at: new Date(Date.now() + 30 * day).toISOString(),
});
const allocation = await call(staff.token, 'POST', `/admin/events/${show.event.id}/allocations`, {
  tickets: 50,
  face_value_cents: 4500,
  min_raw_score: 0,
  registration_opens_at: new Date(Date.now() - 60_000).toISOString(),
  registration_closes_at: new Date(Date.now() + 7 * day).toISOString(),
});
await call(staff.token, 'POST', `/admin/allocations/${allocation.id}/publish`);
const ticket = await fan('mticket');
await sql(
  `INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight, breakdown, computed_at)
   VALUES ($1, $2, 50, 90, false, 0.2, '{}', now())`,
  [ticket.id, artist.id],
);

const values = {
  NEW_FAN: `mnew_${run}`,
  HOME_FAN: home.handle,
  JOURNAL_FAN: (await fan('mjournal')).handle,
  ARTIST_ID: artist.id,
  ARTIST_NAME: artist.name,
  ALBUM_ID: album.id,
  JOURNAL_REVIEW: `Logged by Maestro ${run}`,
  TICKET_FAN: ticket.handle,
  SHOW_ID: show.event.id,
  ACCOUNT_FAN: (await fan('maccount')).handle,
};
for (const [key, value] of Object.entries(values)) console.log(`${key}=${value}`);
