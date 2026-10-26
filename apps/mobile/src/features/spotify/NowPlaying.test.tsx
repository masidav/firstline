import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking } from 'react-native';

import { ApiError } from '../../api/client';
import { NowPlayingBar, NowPlayingProvider, PlayButton } from './NowPlaying';
import { opensInSpotify, progressAt, type SpotifyPlayer } from './player';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const done = () => ({ data: undefined, response: { ok: true, status: 204 } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
const mockState = { status: 'signedIn', me: { handle: 'fan_one' } };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: mockState }) }));
jest.mock('react-native-safe-area-context', () => ({ useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }) }));

const SONG = '4uLU6hMCjMI75M1A2tKUQC';
const URL = `https://open.spotify.com/track/${SONG}`;
const playing: SpotifyPlayer = {
  status: 'playing',
  progress_ms: 30_000,
  device: { name: "Fan's iPhone", type: 'Smartphone' },
  track: {
    spotify_track_id: SONG,
    title: 'Hard Times',
    artists: [{ spotify_artist_id: 'sp-paramore', name: 'Paramore' }],
    album_title: 'After Laughter',
    cover_url: null,
    spotify_url: URL,
    duration_ms: 180_000,
  },
};
const idle = (status: SpotifyPlayer['status']): SpotifyPlayer => ({ status, track: null, progress_ms: null, device: null });

function App() {
  return (
    <NowPlayingProvider>
      <PlayButton spotifyTrackId={SONG} spotifyUrl={URL} title="Hard Times" />
      <PlayButton spotifyTrackId="0bbbbbbbbbbbbbbbbbbbbb" spotifyUrl="https://open.spotify.com/track/0bbbbbbbbbbbbbbbbbbbbb" title="Other Song" />
      <NowPlayingBar />
    </NowPlayingProvider>
  );
}

beforeEach(() => {
  jest.clearAllMocks();
  jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
});

test('the bar shows what Spotify is playing and pauses it', async () => {
  mockApi.GET.mockResolvedValue(ok(playing));
  mockApi.PUT.mockResolvedValue(done());
  render(<App />);

  expect(await screen.findByText('Hard Times')).toBeTruthy();
  expect(screen.getByText(/Paramore · on Fan's iPhone/)).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/me/spotify/player');
  // The feed's button for the same song pauses it too.
  expect(screen.getByRole('button', { name: 'Pause Hard Times' })).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Pause' }));
  await waitFor(() => expect(mockApi.PUT).toHaveBeenCalledWith('/me/spotify/player/pause'));
  expect(await screen.findByRole('button', { name: 'Play' })).toBeTruthy();

  mockApi.POST.mockResolvedValue(done());
  fireEvent.press(screen.getByRole('button', { name: 'Next song' }));
  await waitFor(() => expect(mockApi.POST).toHaveBeenCalledWith('/me/spotify/player/next'));
});

test('Play starts the song on the fan’s Spotify', async () => {
  mockApi.GET.mockResolvedValue(ok(idle('nothing_playing')));
  mockApi.PUT.mockResolvedValue(done());
  render(<App />);
  await waitFor(() => expect(mockApi.GET).toHaveBeenCalled());
  expect(screen.queryByLabelText('Now playing on Spotify')).toBeNull();

  fireEvent.press(screen.getByRole('button', { name: 'Play Other Song' }));
  await waitFor(() =>
    expect(mockApi.PUT).toHaveBeenCalledWith('/me/spotify/player/play', { body: { spotify_track_id: '0bbbbbbbbbbbbbbbbbbbbb' } }),
  );
  expect(Linking.openURL).not.toHaveBeenCalled();
});

test('with Spotify closed everywhere, the song opens in Spotify and the bar says why', async () => {
  mockApi.GET.mockResolvedValue(ok(idle('nothing_playing')));
  mockApi.PUT.mockResolvedValue(problem(409, 'spotify_no_device'));
  render(<App />);
  await waitFor(() => expect(mockApi.GET).toHaveBeenCalled());

  fireEvent.press(screen.getByRole('button', { name: 'Play Hard Times' }));
  await waitFor(() => expect(Linking.openURL).toHaveBeenCalledWith(URL));
  expect(await screen.findByText(/Spotify isn't open on your phone or computer/)).toBeTruthy();
});

test('without the playback permission, Play just opens the song in Spotify', async () => {
  mockApi.GET.mockResolvedValue(ok(idle('reconnect_needed')));
  render(<App />);
  await waitFor(() => expect(mockApi.GET).toHaveBeenCalled());
  await act(async () => {});

  fireEvent.press(screen.getByRole('button', { name: 'Play Hard Times' }));
  await waitFor(() => expect(Linking.openURL).toHaveBeenCalledWith(URL));
  expect(mockApi.PUT).not.toHaveBeenCalled();
  expect(screen.queryByLabelText('Now playing on Spotify')).toBeNull();
});

test('outside the app shell a Play button opens the song, as before', async () => {
  render(<PlayButton spotifyTrackId={SONG} spotifyUrl={URL} title="Hard Times" />);
  fireEvent.press(screen.getByRole('button', { name: 'Play Hard Times' }));
  await waitFor(() => expect(Linking.openURL).toHaveBeenCalledWith(URL));
});

test('progress counts on while playing and stops at the end of the song', () => {
  expect(progressAt(playing, 5_000)).toBe(35_000);
  expect(progressAt({ ...playing, status: 'paused' }, 5_000)).toBe(30_000);
  expect(progressAt(playing, 999_999)).toBe(180_000);
  expect(progressAt(idle('nothing_playing'), 1_000)).toBeNull();
});

test('only connection and device problems open the song instead', () => {
  expect(opensInSpotify(new ApiError(409, { code: 'spotify_premium_required' }))).toBe(true);
  expect(opensInSpotify(new ApiError(503, { code: 'spotify_unavailable' }))).toBe(false);
  expect(opensInSpotify(new TypeError('offline'))).toBe(true);
});
