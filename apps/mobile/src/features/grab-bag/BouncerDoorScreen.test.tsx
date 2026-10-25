import { fireEvent, render, screen } from '@testing-library/react-native';
import { Linking } from 'react-native';

import { BouncerDoorScreen } from './BouncerDoorScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'ben' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));

const song = {
  id: 'g1',
  position: 2,
  spotify_track_id: '1aaaaaaaaaaaaaaaaaaaaa',
  title: 'Cowboy Nudes',
  artist: { id: 'a1', name: 'Geese', image_url: null },
  artist_status: 'active',
  album_title: '3D Country',
  cover_url: null,
  spotify_url: 'https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa',
  added_at: '2026-10-21T10:00:00Z',
};
const owner = { handle: 'ana', deleted: false, display_name: 'Ana', avatar_url: null };

beforeEach(() => jest.clearAllMocks());

test('shows the drawn song with whose it is, opens it on Spotify, and draws another', async () => {
  mockApi.GET.mockResolvedValueOnce(ok({ song, owner, following: 3, songs: 7 })).mockResolvedValueOnce(
    ok({ song: { ...song, id: 'g2', title: '2122' }, owner: { ...owner, display_name: null }, following: 3, songs: 7 }),
  );
  const openUrl = jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
  render(<BouncerDoorScreen />);

  expect(await screen.findByText('Cowboy Nudes')).toBeTruthy();
  expect(screen.getByText("From Ana's grab bag")).toBeTruthy();
  fireEvent.press(screen.getByRole('link', { name: 'Open Cowboy Nudes on Spotify' }));
  expect(openUrl).toHaveBeenCalledWith(song.spotify_url);

  fireEvent.press(screen.getByRole('button', { name: 'Draw another' }));
  expect(await screen.findByText('2122')).toBeTruthy();
  expect(screen.getByText("From @ana's grab bag")).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledTimes(2);
});

test('with nobody followed, it points to People; with empty bags, it says so', async () => {
  mockApi.GET.mockResolvedValue(ok({ song: null, owner: null, following: 0, songs: 0 }));
  render(<BouncerDoorScreen />);
  fireEvent.press(await screen.findByRole('button', { name: 'Find fans to follow' }));
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/people');

  mockApi.GET.mockResolvedValue(ok({ song: null, owner: null, following: 2, songs: 0 }));
  render(<BouncerDoorScreen />);
  expect(await screen.findByText(/Nobody you follow has put a song/)).toBeTruthy();
});
