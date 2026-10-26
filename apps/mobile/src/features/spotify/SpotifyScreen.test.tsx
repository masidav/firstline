import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { SpotifyScreen } from './SpotifyScreen';
import { SPOTIFY_SCOPES } from './spotifyConnect';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockUpdateMe = jest.fn();
// Stable across renders, like the real context value.
const mockSession = { api: mockApi, updateMe: mockUpdateMe };

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession }),
}));
jest.mock('../../config', () => ({ config: { spotifyClientId: 'client-1' } }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn() } }));
jest.mock('./spotifyRedirect', () => ({ startSpotifyConnect: jest.fn() }));

beforeEach(() => jest.clearAllMocks());

test('not connected offers to connect', async () => {
  mockApi.GET.mockResolvedValue(ok({ connected: false, sync_state: 'idle' }));
  const { startSpotifyConnect } = jest.requireMock('./spotifyRedirect');
  startSpotifyConnect.mockResolvedValue('redirecting');
  render(<SpotifyScreen />);

  await screen.findByText('Not connected.');
  fireEvent.press(screen.getByRole('button', { name: 'Connect Spotify' }));

  expect(startSpotifyConnect).toHaveBeenCalledWith('client-1', expect.any(Function));
  // On the web the page is leaving, so the button stays busy.
  await waitFor(() => expect(screen.getByRole('button', { name: 'Connect Spotify' })).toBeDisabled());
});

test('a native connect that finishes in place shows the new status and sends the code to the API', async () => {
  mockApi.GET.mockResolvedValue(ok({ connected: false, sync_state: 'idle' }));
  mockApi.PUT.mockResolvedValue(ok({ connected: true, sync_state: 'queued' }));
  const { startSpotifyConnect } = jest.requireMock('./spotifyRedirect');
  startSpotifyConnect.mockImplementation(async (_clientId: string, connect: (body: unknown) => Promise<unknown>) => ({
    ok: true,
    status: await connect({ code: 'c', code_verifier: 'v', redirect_uri: 'fandomscore://spotify-callback' }),
  }));
  render(<SpotifyScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Spotify' }));

  expect(await screen.findByText('Syncing your library…')).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/me/spotify', {
    body: { code: 'c', code_verifier: 'v', redirect_uri: 'fandomscore://spotify-callback' },
  });
});

test('closing the native browser leaves things as they were', async () => {
  mockApi.GET.mockResolvedValue(ok({ connected: false, sync_state: 'idle' }));
  const { startSpotifyConnect } = jest.requireMock('./spotifyRedirect');
  startSpotifyConnect.mockResolvedValue('cancelled');
  render(<SpotifyScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Spotify' }));

  await waitFor(() => expect(screen.getByRole('button', { name: 'Connect Spotify' })).toBeEnabled());
  expect(screen.getByText('Not connected.')).toBeTruthy();
});

test('a failed native connect shows why', async () => {
  mockApi.GET.mockResolvedValue(ok({ connected: false, sync_state: 'idle' }));
  const { startSpotifyConnect } = jest.requireMock('./spotifyRedirect');
  startSpotifyConnect.mockResolvedValue({ ok: false, message: "You didn't allow access on Spotify." });
  render(<SpotifyScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Spotify' }));

  expect(await screen.findByText("You didn't allow access on Spotify.")).toBeTruthy();
});

test('sync now queues a sync and shows it', async () => {
  mockApi.GET.mockResolvedValue(
    ok({ connected: true, sync_state: 'idle', last_synced_at: '2026-10-18T12:00:00Z', tracked_artists_found: 2 }),
  );
  mockApi.POST.mockResolvedValue(ok({ connected: true, sync_state: 'queued' }, 202));
  render(<SpotifyScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Sync now' }));

  expect(await screen.findByText('Syncing your library…')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Syncing…' })).toBeDisabled();
  expect(mockUpdateMe).toHaveBeenLastCalledWith({ spotify: { connected: true, sync_state: 'queued' } });
});

test('the 15-minute manual sync limit is explained', async () => {
  mockApi.GET.mockResolvedValue(ok({ connected: true, sync_state: 'idle' }));
  mockApi.POST.mockResolvedValue(problem(429, 'rate_limited'));
  render(<SpotifyScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Sync now' }));

  expect(await screen.findByText(/once every 15 minutes/)).toBeTruthy();
});

test('reauth_required asks to connect again', async () => {
  mockApi.GET.mockResolvedValue(ok({ connected: true, sync_state: 'reauth_required' }));
  render(<SpotifyScreen />);

  expect(await screen.findByRole('button', { name: 'Connect again' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Sync now' })).toBeNull();
});

test('disconnecting asks first, then clears the connection', async () => {
  mockApi.GET.mockResolvedValue(ok({ connected: true, sync_state: 'idle' }));
  mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
  render(<SpotifyScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Disconnect Spotify' }));
  fireEvent.press(screen.getByRole('button', { name: 'Yes, disconnect' }));

  expect(await screen.findByText('Not connected.')).toBeTruthy();
  expect(mockApi.DELETE).toHaveBeenCalledWith('/me/spotify');
});

test('a connection made without the follow permission asks to connect again', async () => {
  mockApi.GET.mockResolvedValue(
    ok({ connected: true, sync_state: 'idle', scopes: ['user-library-read', 'user-top-read'], last_synced_at: '2026-10-18T12:00:00Z' }),
  );
  render(<SpotifyScreen />);

  expect(await screen.findByText(/count the artists you follow, show what's playing/)).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Connect again' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Sync now' })).toBeNull();
});

test('a connection made before the playback permissions asks to connect again', async () => {
  const before = SPOTIFY_SCOPES.filter((s) => !s.includes('playback'));
  mockApi.GET.mockResolvedValue(ok({ connected: true, sync_state: 'idle', scopes: before, last_synced_at: '2026-10-18T12:00:00Z' }));
  render(<SpotifyScreen />);

  expect(await screen.findByRole('button', { name: 'Connect again' })).toBeTruthy();
});
