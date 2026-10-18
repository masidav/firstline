import { fireEvent, render, screen } from '@testing-library/react-native';

import { SpotifyScreen } from './SpotifyScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), DELETE: jest.fn() };
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
  render(<SpotifyScreen />);

  await screen.findByText('Not connected.');
  fireEvent.press(screen.getByRole('button', { name: 'Connect Spotify' }));

  const { startSpotifyConnect } = jest.requireMock('./spotifyRedirect');
  expect(startSpotifyConnect).toHaveBeenCalledWith('client-1');
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
