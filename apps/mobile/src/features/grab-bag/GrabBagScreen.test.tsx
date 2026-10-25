import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { firstEmptySlot, GrabBagScreen } from './GrabBagScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });
const mockApi = { GET: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'ana' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));

const artist = { id: 'a1', name: 'Geese', image_url: null };
const owner = { handle: 'ana', deleted: false, display_name: null, avatar_url: null };
const song = (position: number, title: string, status = 'active') => ({
  id: `g${position}`,
  position,
  spotify_track_id: `${position}aaaaaaaaaaaaaaaaaaaaa`,
  title,
  artist,
  artist_status: status,
  album_title: '3D Country',
  cover_url: null,
  spotify_url: `https://open.spotify.com/track/${position}aaaaaaaaaaaaaaaaaaaaa`,
  added_at: '2026-10-21T10:00:00Z',
});
const found = {
  spotify_track_id: '9aaaaaaaaaaaaaaaaaaaaa',
  title: 'Brand New Song',
  artists: [{ spotify_artist_id: 'sp-new', name: 'Newcomer', artist_id: null }],
  album_title: 'Debut',
  cover_url: null,
  spotify_url: 'https://open.spotify.com/track/9aaaaaaaaaaaaaaaaaaaaa',
};

beforeEach(() => jest.clearAllMocks());

test('shows the five slots, adds a found song to the first empty one, and removes a song', async () => {
  const bag = { owner, items: [song(1, 'Cowboy Nudes'), song(3, '2122', 'onboarding')], slots: 5 };
  mockApi.GET.mockImplementation(async (path: string) => (path === '/me/grab-bag' ? ok(bag) : ok({ items: [found] })));
  mockApi.PUT.mockResolvedValue(ok({ ...bag, items: [...bag.items, { ...song(2, 'Brand New Song'), spotify_track_id: found.spotify_track_id }] }));
  mockApi.DELETE.mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
  render(<GrabBagScreen />);

  expect(await screen.findByText('Cowboy Nudes')).toBeTruthy();
  expect(screen.getByText('Song 2 · empty')).toBeTruthy();
  expect(screen.getByText('Geese · 3D Country · artist getting ready')).toBeTruthy();
  expect(screen.getAllByText(/· empty/)).toHaveLength(3);

  const input = screen.getByLabelText('Search songs on Spotify');
  fireEvent.changeText(input, 'brand');
  fireEvent(input, 'submitEditing');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Brand New Song' }));

  expect(mockApi.PUT).toHaveBeenCalledWith('/me/grab-bag/{position}', {
    params: { path: { position: 2 } },
    body: { spotify_track_id: found.spotify_track_id },
  });
  expect(await screen.findByText(/Newcomer is new here/)).toBeTruthy();
  expect(screen.getByText('In your bag')).toBeTruthy();
  expect(screen.queryByText('Song 2 · empty')).toBeNull();

  fireEvent.press(screen.getByRole('button', { name: 'Remove 2122' }));
  expect(mockApi.DELETE).toHaveBeenCalledWith('/me/grab-bag/{position}', { params: { path: { position: 3 } } });
  await waitFor(() => expect(screen.getByText('Song 3 · empty')).toBeTruthy());
});

test('a full bag asks for room, and Spotify being down is explained', async () => {
  const full = { owner, items: [1, 2, 3, 4, 5].map((n) => song(n, `Track ${n}`)), slots: 5 };
  mockApi.GET.mockImplementation(async (path: string) => (path === '/me/grab-bag' ? ok(full) : ok({ items: [found] })));
  render(<GrabBagScreen />);

  expect(await screen.findByText('Track 5')).toBeTruthy();
  const input = screen.getByLabelText('Search songs on Spotify');
  fireEvent.changeText(input, 'brand');
  fireEvent(input, 'submitEditing');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Brand New Song' }));
  expect(await screen.findByText(/grab bag is full/)).toBeTruthy();
  expect(mockApi.PUT).not.toHaveBeenCalled();

  mockApi.GET.mockImplementation(async (path: string) => (path === '/me/grab-bag' ? ok(full) : problem(503, 'spotify_unavailable')));
  fireEvent.changeText(input, 'other');
  fireEvent(input, 'submitEditing');
  expect(await screen.findByText(/Spotify isn't responding/)).toBeTruthy();
});

test('firstEmptySlot finds the gap', () => {
  expect(firstEmptySlot({ items: [], slots: 5 })).toBe(1);
  expect(firstEmptySlot({ items: [song(1, 'a'), song(2, 'b')] as never, slots: 5 })).toBe(3);
  expect(firstEmptySlot({ items: [1, 2, 3, 4, 5].map((n) => song(n, 'x')) as never, slots: 5 })).toBeNull();
});
