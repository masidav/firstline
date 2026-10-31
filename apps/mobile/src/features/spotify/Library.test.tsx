import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { LikeButton, SpotifyLibraryProvider } from './Library';
import { NowPlayingBar, NowPlayingProvider } from './NowPlaying';
import { SongControls } from './SongControls';
import type { SpotifyLibrary } from './Library';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const done = () => ({ data: undefined, response: { ok: true, status: 204 } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
const mockState = { status: 'signedIn', me: { handle: 'fan_one' } };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: mockState }) }));
jest.mock('react-native-safe-area-context', () => ({ useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }) }));

const SONG = '4uLU6hMCjMI75M1A2tKUQC';
const OTHER = '0bbbbbbbbbbbbbbbbbbbbb';
const ALBUM = '6kZ42qRrzov54LcAk4onW9';
const nothingPlaying = { status: 'nothing_playing', track: null, progress_ms: null, device: null };
const library = (status: SpotifyLibrary['status'], saved: string[] = []): SpotifyLibrary => ({
  status,
  tracks: status === 'ok' ? [SONG, OTHER].map((id) => ({ spotify_track_id: id, saved: saved.includes(id) })) : [],
  albums: status === 'ok' ? [{ spotify_album_id: ALBUM, saved: saved.includes(ALBUM) }] : [],
});

function App() {
  return (
    <NowPlayingProvider>
      <SpotifyLibraryProvider>
        <SongControls spotifyTrackId={SONG} spotifyUrl={`https://open.spotify.com/track/${SONG}`} title="Hard Times" />
        <LikeButton kind="track" id={OTHER} title="Other Song" compact />
        <LikeButton kind="album" id={ALBUM} title="After Laughter" />
        <NowPlayingBar />
      </SpotifyLibraryProvider>
    </NowPlayingProvider>
  );
}

function answer(state: SpotifyLibrary) {
  mockApi.GET.mockImplementation((path: string) => Promise.resolve(ok(path === '/me/spotify/library' ? state : nothingPlaying)));
}

beforeEach(() => {
  jest.clearAllMocks();
});

test('every Like button on screen is answered by one request, and a like saves the song', async () => {
  answer(library('ok', [ALBUM]));
  mockApi.PUT.mockResolvedValue(done());
  render(<App />);

  expect(await screen.findByRole('button', { name: 'Unlike After Laughter' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Like Hard Times' })).toBeTruthy();
  expect(screen.getByText('♡')).toBeTruthy();
  const asks = mockApi.GET.mock.calls.filter(([path]) => path === '/me/spotify/library');
  expect(asks).toHaveLength(1);
  expect(asks[0][1]).toEqual({ params: { query: { track_ids: `${SONG},${OTHER}`, album_ids: ALBUM } } });

  fireEvent.press(screen.getByRole('button', { name: 'Like Hard Times' }));
  expect(await screen.findByRole('button', { name: 'Unlike Hard Times' })).toBeTruthy();
  await waitFor(() =>
    expect(mockApi.PUT).toHaveBeenCalledWith('/me/spotify/library/tracks/{spotifyTrackId}', { params: { path: { spotifyTrackId: SONG } } }),
  );
  expect(screen.getAllByText('♥ Liked')).toHaveLength(2);   // the song and the album

  mockApi.DELETE.mockResolvedValue(done());
  fireEvent.press(screen.getByRole('button', { name: 'Unlike After Laughter' }));
  await waitFor(() =>
    expect(mockApi.DELETE).toHaveBeenCalledWith('/me/spotify/library/albums/{spotifyAlbumId}', { params: { path: { spotifyAlbumId: ALBUM } } }),
  );
  expect(screen.getByRole('button', { name: 'Like After Laughter' })).toBeTruthy();
});

test('a like Spotify refuses is undone and the bar says so', async () => {
  answer(library('ok'));
  mockApi.PUT.mockResolvedValue(problem(409, 'spotify_library_refused'));
  render(<App />);
  await screen.findByRole('button', { name: 'Like Hard Times' });

  fireEvent.press(screen.getByRole('button', { name: 'Like Hard Times' }));
  expect(await screen.findByText(/Spotify didn't accept that/)).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Like Hard Times' })).toBeTruthy();
});

test('without a Spotify connection there are no Like buttons; an old connection asks to connect again', async () => {
  answer(library('not_connected'));
  const first = render(<App />);
  await waitFor(() => expect(mockApi.GET).toHaveBeenCalledWith('/me/spotify/library', expect.anything()));
  await act(async () => {});
  expect(screen.queryByRole('button', { name: /Like/ })).toBeNull();
  expect(screen.getByRole('button', { name: 'Play Hard Times' })).toBeTruthy();
  first.unmount();

  mockApi.GET.mockClear();
  answer(library('reconnect_needed'));
  render(<App />);
  await waitFor(() => expect(mockApi.GET).toHaveBeenCalledWith('/me/spotify/library', expect.anything()));
  await act(async () => {});
  fireEvent.press(screen.getByRole('button', { name: 'Like Hard Times' }));
  expect(await screen.findByText(/Connect Spotify again in your profile to like songs here/)).toBeTruthy();
  expect(mockApi.PUT).not.toHaveBeenCalled();
});

test('outside the app shell there is no Like button', () => {
  render(<LikeButton kind="track" id={SONG} title="Hard Times" />);
  expect(screen.queryByRole('button')).toBeNull();
});
