import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { notConnected } from './musicConnect';
import { MusicScreen } from './MusicScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
// Stable across renders, like the real context value.
const mockSession = { api: mockApi };
const mockConfig = { tidalClientId: 'tidal-1', appleMusicEnabled: true };

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession }),
}));
jest.mock('../../config', () => ({
  get config() {
    return mockConfig;
  },
}));
jest.mock('expo-router', () => ({ router: { replace: jest.fn() } }));
jest.mock('./tidalRedirect', () => ({ startTidalConnect: jest.fn() }));
jest.mock('./appleMusicConnect', () => ({ connectAppleMusic: jest.fn() }));

const { startTidalConnect } = jest.requireMock<{ startTidalConnect: jest.Mock }>('./tidalRedirect');
const { connectAppleMusic } = jest.requireMock<{ connectAppleMusic: jest.Mock }>('./appleMusicConnect');

const appleSynced = {
  ...notConnected('apple_music'),
  connected: true,
  connected_at: '2026-10-18T11:00:00Z',
  last_synced_at: '2026-10-18T12:00:00Z',
  tracked_artists_found: 3,
};
const tidalSynced = { ...notConnected('tidal'), connected: true, last_synced_at: '2026-10-18T12:00:00Z', tracked_artists_found: 1 };

/** GET /me/music, then GET /me/music/{provider} for the polls. */
function listing(...items: unknown[]) {
  mockApi.GET.mockImplementation(async (path: string, options?: { params: { path: { provider: string } } }) =>
    path === '/me/music'
      ? ok({ items })
      : ok((items as { provider: string }[]).find((item) => item.provider === options?.params.path.provider)),
  );
}

beforeEach(() => {
  jest.clearAllMocks();
  mockConfig.tidalClientId = 'tidal-1';
  mockConfig.appleMusicEnabled = true;
});

test('shows one section per provider from GET /me/music', async () => {
  listing(appleSynced);
  render(<MusicScreen />);

  expect(await screen.findByText(/Found 3 tracked artists/)).toBeTruthy();
  expect(screen.getByRole('header', { name: 'Apple Music' })).toBeTruthy();
  expect(screen.getByRole('header', { name: 'Tidal' })).toBeTruthy();
  expect(screen.getByText('Not connected.')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Sync Apple Music now' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Disconnect Apple Music' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Connect Tidal' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Connect Apple Music' })).toBeNull();
  expect(mockApi.GET).toHaveBeenCalledWith('/me/music');
});

test('connecting Tidal starts the PKCE redirect with the client ID', async () => {
  listing();
  startTidalConnect.mockResolvedValue('redirecting');
  render(<MusicScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Tidal' }));

  expect(startTidalConnect).toHaveBeenCalledWith('tidal-1', expect.any(Function));
  // On the web the page is leaving, so the button stays busy.
  await waitFor(() => expect(screen.getByRole('button', { name: 'Connect Tidal' })).toBeDisabled());
});

test('a native Tidal connect that finishes in place sends the code to PUT /me/music/tidal and shows the status', async () => {
  listing();
  mockApi.PUT.mockResolvedValue(ok({ ...notConnected('tidal'), connected: true, sync_state: 'queued' }));
  startTidalConnect.mockImplementation(async (_clientId: string, connect: (body: unknown) => Promise<unknown>) => ({
    ok: true,
    status: await connect({ code: 'c', code_verifier: 'v', redirect_uri: 'fandomscore://tidal-callback' }),
  }));
  render(<MusicScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Tidal' }));

  expect(await screen.findByText('Syncing your library…')).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/me/music/{provider}', {
    params: { path: { provider: 'tidal' } },
    body: { code: 'c', code_verifier: 'v', redirect_uri: 'fandomscore://tidal-callback' },
  });
});

test('connecting Apple Music goes through MusicKit and shows the new status', async () => {
  listing();
  connectAppleMusic.mockResolvedValue({ ok: true, status: { ...notConnected('apple_music'), connected: true, sync_state: 'queued' } });
  render(<MusicScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Apple Music' }));

  expect(await screen.findByText('Syncing your library…')).toBeTruthy();
  expect(connectAppleMusic).toHaveBeenCalledWith(mockApi);
});

test('a cancelled Apple Music connect leaves things as they were; a failed one says why', async () => {
  listing();
  connectAppleMusic.mockResolvedValueOnce('cancelled');
  render(<MusicScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Apple Music' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Connect Apple Music' })).toBeEnabled());

  connectAppleMusic.mockResolvedValueOnce({ ok: false, message: 'Apple Music connects from the web app for now.' });
  fireEvent.press(screen.getByRole('button', { name: 'Connect Apple Music' }));

  expect(await screen.findByText('Apple Music connects from the web app for now.')).toBeTruthy();
});

test('sync now posts to the provider, shows the sync, and polls until it is done', async () => {
  jest.useFakeTimers();
  try {
    listing(tidalSynced);
    mockApi.POST.mockResolvedValue(ok({ ...tidalSynced, sync_state: 'queued' }, 202));
    render(<MusicScreen />);

    fireEvent.press(await screen.findByRole('button', { name: 'Sync Tidal now' }));

    expect(await screen.findByText('Syncing your library…')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Syncing Tidal' })).toBeDisabled();
    expect(mockApi.POST).toHaveBeenCalledWith('/me/music/{provider}/sync', { params: { path: { provider: 'tidal' } } });

    // Three seconds later the screen asks for that provider again; the sync has finished.
    listing({ ...tidalSynced, tracked_artists_found: 4 });
    await act(async () => {
      jest.advanceTimersByTime(3000);
    });

    expect(await screen.findByText(/Found 4 tracked artists/)).toBeTruthy();
    expect(mockApi.GET).toHaveBeenCalledWith('/me/music/{provider}', { params: { path: { provider: 'tidal' } } });
  } finally {
    jest.useRealTimers();
  }
});

test('the 15-minute manual sync limit is explained', async () => {
  listing(appleSynced);
  mockApi.POST.mockResolvedValue(problem(429, 'rate_limited'));
  render(<MusicScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Sync Apple Music now' }));

  expect(await screen.findByText(/once every 15 minutes/)).toBeTruthy();
});

test('reauth_required asks to connect again', async () => {
  listing({ ...tidalSynced, sync_state: 'reauth_required' });
  render(<MusicScreen />);

  expect(await screen.findByRole('button', { name: 'Connect Tidal again' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Sync Tidal now' })).toBeNull();
});

test('disconnecting asks first, then deletes only that provider', async () => {
  listing(appleSynced, tidalSynced);
  mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
  render(<MusicScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Disconnect Tidal' }));
  expect(screen.getByText(/Disconnect Tidal\? The library data synced from Tidal is deleted/)).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Yes, disconnect Tidal' }));

  expect(await screen.findByText('Not connected.')).toBeTruthy();
  expect(mockApi.DELETE).toHaveBeenCalledWith('/me/music/{provider}', { params: { path: { provider: 'tidal' } } });
  expect(mockApi.DELETE).toHaveBeenCalledTimes(1);
  // Apple Music is untouched.
  expect(screen.getByText(/Found 3 tracked artists/)).toBeTruthy();
});

test('a provider this build is not set up for shows a note instead of a Connect button', async () => {
  mockConfig.tidalClientId = '';
  mockConfig.appleMusicEnabled = false;
  listing();
  render(<MusicScreen />);

  expect(await screen.findByText(/Tidal isn't set up for this build \(EXPO_PUBLIC_TIDAL_CLIENT_ID\)/)).toBeTruthy();
  expect(screen.getByText(/Apple Music isn't set up for this build \(EXPO_PUBLIC_APPLE_MUSIC_ENABLED\)/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Connect Tidal' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Connect Apple Music' })).toBeNull();
});
