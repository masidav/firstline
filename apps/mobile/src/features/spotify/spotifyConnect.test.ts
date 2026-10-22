/**
 * @jest-environment node
 */
import { ApiError } from '../../api/client';
import { codeChallenge, randomString } from './pkce';
import { authorizeUrl, completeConnect, describeStatus, SPOTIFY_SCOPES } from './spotifyConnect';

const pending = { state: 'state-123', verifier: 'v'.repeat(64), redirectUri: 'http://127.0.0.1:8081/spotify-callback' };
const connected = { connected: true, sync_state: 'queued' as const };

test('code challenge matches the RFC 7636 example', async () => {
  await expect(codeChallenge('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')).resolves.toBe(
    'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
  );
});

test('verifiers are random unreserved strings', () => {
  const a = randomString(64);
  expect(a).toMatch(/^[A-Za-z0-9\-._~]{64}$/);
  expect(randomString(64)).not.toBe(a);
});

test('the authorize URL asks for every scope with PKCE', () => {
  const url = new URL(authorizeUrl({ clientId: 'client-1', redirectUri: pending.redirectUri, state: 's', challenge: 'c' }));

  expect(url.origin + url.pathname).toBe('https://accounts.spotify.com/authorize');
  expect(Object.fromEntries(url.searchParams)).toEqual({
    client_id: 'client-1',
    response_type: 'code',
    redirect_uri: pending.redirectUri,
    code_challenge_method: 'S256',
    code_challenge: 'c',
    scope: SPOTIFY_SCOPES.join(' '),
    state: 's',
  });
});

test('a matching callback sends code, verifier, and redirect URI to the API', async () => {
  const connect = jest.fn().mockResolvedValue(connected);

  const result = await completeConnect({ code: 'abc', state: 'state-123' }, pending, connect);

  expect(result).toEqual({ ok: true, status: connected });
  expect(connect).toHaveBeenCalledWith({ code: 'abc', code_verifier: pending.verifier, redirect_uri: pending.redirectUri });
});

test.each([
  ['a different state', { code: 'abc', state: 'forged' }, pending],
  ['no attempt started in this tab', { code: 'abc', state: 'state-123' }, null],
  ['no code', { state: 'state-123' }, pending],
])('a callback with %s is not sent', async (_case, params, start) => {
  const connect = jest.fn();

  const result = await completeConnect(params, start, connect);

  expect(result).toEqual({ ok: false, message: "This Spotify sign-in expired or didn't start here. Try connecting again." });
  expect(connect).not.toHaveBeenCalled();
});

test('declining on Spotify is explained', async () => {
  const result = await completeConnect({ error: 'access_denied', state: 'state-123' }, pending, jest.fn());
  expect(result).toEqual({ ok: false, message: "You didn't allow access on Spotify." });
});

test.each([
  [new ApiError(409, { code: 'spotify_in_use' }), 'This Spotify account is already connected to another Fandom Score account.'],
  [
    new ApiError(422, { code: 'validation_failed', errors: [{ field: 'code', message: 'Spotify did not grant every scope.' }] }),
    'Spotify did not grant every scope.',
  ],
  [new ApiError(503, { code: 'spotify_unavailable' }), "Spotify isn't available right now. Try again in a few minutes."],
])('API errors become readable messages', async (error, message) => {
  const result = await completeConnect({ code: 'abc', state: 'state-123' }, pending, jest.fn().mockRejectedValue(error));
  expect(result).toEqual({ ok: false, message });
});

test('status lines', () => {
  expect(describeStatus({ connected: false, sync_state: 'idle' })).toBe('Not connected.');
  expect(describeStatus({ connected: true, sync_state: 'running' })).toBe('Syncing your library…');
  expect(describeStatus({ connected: true, sync_state: 'failed', sync_error: 'spotify_forbidden' })).toContain('allowlisted');
  expect(
    describeStatus({ connected: true, sync_state: 'idle', last_synced_at: '2026-10-18T12:00:00Z', tracked_artists_found: 3 }),
  ).toContain('Found 3 tracked artists');
  const withAdded = describeStatus({
    connected: true,
    sync_state: 'idle',
    last_synced_at: '2026-10-18T12:00:00Z',
    tracked_artists_found: 3,
    top_artists_added: 5,
  });
  expect(withAdded).toContain('Found 3 tracked artists');
  expect(withAdded).toContain('Added 5 of your top artists');
});
