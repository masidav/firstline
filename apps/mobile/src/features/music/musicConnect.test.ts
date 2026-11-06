/**
 * @jest-environment node
 */
import { ApiError } from '../../api/client';
import {
  completeTidalConnect,
  connectErrorMessage,
  describeStatus,
  isSyncing,
  type MusicStatus,
  notConnected,
  TIDAL_SCOPES,
  tidalAuthorizeUrl,
} from './musicConnect';

const pending = { state: 'state-123', verifier: 'v'.repeat(64), redirectUri: 'http://127.0.0.1:8081/tidal-callback' };
const connected: MusicStatus = { ...notConnected('tidal'), connected: true, sync_state: 'queued' };

test('the Tidal authorize URL asks for every scope with PKCE', () => {
  const url = new URL(tidalAuthorizeUrl({ clientId: 'client-1', redirectUri: pending.redirectUri, state: 's', challenge: 'c' }));

  expect(url.origin + url.pathname).toBe('https://login.tidal.com/authorize');
  expect(Object.fromEntries(url.searchParams)).toEqual({
    response_type: 'code',
    client_id: 'client-1',
    redirect_uri: pending.redirectUri,
    code_challenge_method: 'S256',
    code_challenge: 'c',
    scope: 'user.read collection.read playlists.read',
    state: 's',
  });
  expect(TIDAL_SCOPES).toEqual(['user.read', 'collection.read', 'playlists.read']);
});

test('a matching callback sends code, verifier, and redirect URI to the API', async () => {
  const connect = jest.fn().mockResolvedValue(connected);

  const result = await completeTidalConnect({ code: 'abc', state: 'state-123' }, pending, connect);

  expect(result).toEqual({ ok: true, status: connected });
  expect(connect).toHaveBeenCalledWith({ code: 'abc', code_verifier: pending.verifier, redirect_uri: pending.redirectUri });
});

test.each([
  ['a different state', { code: 'abc', state: 'forged' }, pending],
  ['no attempt started in this tab', { code: 'abc', state: 'state-123' }, null],
  ['no code', { state: 'state-123' }, pending],
])('a callback with %s is not sent', async (_case, params, start) => {
  const connect = jest.fn();

  const result = await completeTidalConnect(params, start, connect);

  expect(result).toEqual({ ok: false, message: "This Tidal sign-in expired or didn't start here. Try connecting again." });
  expect(connect).not.toHaveBeenCalled();
});

test('declining on Tidal is explained', async () => {
  const result = await completeTidalConnect({ error: 'access_denied', state: 'state-123' }, pending, jest.fn());
  expect(result).toEqual({ ok: false, message: "You didn't allow access on Tidal." });
});

test.each([
  [new ApiError(409, { code: 'music_in_use' }), 'This Tidal account is already connected to another Fandom Score account.'],
  [
    new ApiError(422, { code: 'validation_failed', errors: [{ field: 'code', message: 'Tidal did not accept the code.' }] }),
    'Tidal did not accept the code.',
  ],
  [new ApiError(503, { code: 'music_unavailable' }), /Tidal isn't available right now/],
])('API errors from the Tidal connect become readable messages', async (error, message) => {
  const result = await completeTidalConnect({ code: 'abc', state: 'state-123' }, pending, jest.fn().mockRejectedValue(error));
  expect(result).toMatchObject({ ok: false, message: expect.stringMatching(message) });
});

test('every music_* problem code has a message, with the provider named when known', () => {
  expect(connectErrorMessage(new ApiError(409, { code: 'music_not_connected' }), 'apple_music')).toBe("Apple Music isn't connected. Connect it first.");
  expect(connectErrorMessage(new ApiError(409, { code: 'music_reauth_required' }), 'tidal')).toBe(
    'Tidal stopped accepting this connection. Connect again to keep syncing.',
  );
  expect(connectErrorMessage(new ApiError(409, { code: 'music_in_use' }))).toBe('This account is already connected to another Fandom Score account.');
  expect(connectErrorMessage(new ApiError(409, { code: 'music_not_connected' }))).toBe("This service isn't connected. Connect it first.");
  expect(connectErrorMessage(new ApiError(503, { code: 'music_unavailable' }), 'apple_music')).toMatch(/Apple Music isn't available right now/);
  expect(
    connectErrorMessage(
      new ApiError(422, { code: 'validation_failed', errors: [{ field: 'music_user_token', message: 'Apple rejected the token.' }] }),
      'apple_music',
    ),
  ).toBe('Apple rejected the token.');
  expect(connectErrorMessage(new Error('offline'))).toBe("Can't reach the server. Check your connection and try again.");
});

test('status lines', () => {
  expect(describeStatus(notConnected('apple_music'))).toBe('Not connected.');
  expect(describeStatus({ ...connected, sync_state: 'running' })).toBe('Syncing your library…');
  expect(describeStatus({ ...connected, sync_state: 'reauth_required' })).toBe(
    'Tidal stopped accepting this connection. Connect again to keep syncing.',
  );
  expect(describeStatus({ ...connected, sync_state: 'failed', sync_error: 'provider_forbidden' })).toContain('Tidal refused this account');
  expect(describeStatus({ ...connected, sync_state: 'failed', sync_error: 'internal_error' })).toContain("didn't finish");
  expect(describeStatus({ ...connected, sync_state: 'idle' })).toBe('Connected. Waiting for the first sync.');
  expect(describeStatus({ ...connected, sync_state: 'idle', last_synced_at: '2026-10-18T12:00:00Z', tracked_artists_found: 3 })).toContain(
    'Found 3 tracked artists',
  );
});

test('isSyncing covers queued and running only', () => {
  expect(isSyncing({ ...connected, sync_state: 'queued' })).toBe(true);
  expect(isSyncing({ ...connected, sync_state: 'running' })).toBe(true);
  expect(isSyncing({ ...connected, sync_state: 'idle' })).toBe(false);
  expect(isSyncing(null)).toBe(false);
});
