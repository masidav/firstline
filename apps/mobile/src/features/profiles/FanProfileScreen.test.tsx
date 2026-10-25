import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking } from 'react-native';

import { FanProfileScreen, profileBandLinks, standingLine } from './FanProfileScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'cat' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));

const profile = {
  handle: 'ana', deleted: false, display_name: 'Ana', avatar_url: null, created_at: '2026-01-01T00:00:00Z', log_count: 1, list_count: 0,
  followed_by_me: false, follows_me: true, follower_count: 3, following_count: 5, is_artist: true, grab_bag_count: 1, is_me: false,
  can_see_follower_content: false, band_url: 'https://ana.example.test', instagram_handle: 'anaband', x_handle: null, answer_count: 1,
};
const log = { id: 'l1', user: { handle: 'ana', deleted: false }, album: { id: 'al1', title: '3D Country', album_type: 'album', cover_url: null,
  artist: { id: 'a1', name: 'Geese', image_url: null } }, rating: 9, listened_on: '2026-10-20', is_relisten: false, visibility: 'public', created_at: '2026-10-20T00:00:00Z' };
const answers = { items: [{ answer: { id: 'pa1', prompt_id: 'p1', user: { handle: 'ana', deleted: false }, body: 'The Velvet Ropes', visibility: 'public',
  created_at: '2026-10-25T00:00:00Z', updated_at: '2026-10-25T00:00:00Z', mine: false, following: false }, prompt_text: 'Best band name?', day: '2026-10-25' }] };
const stats = { artists: [{ artist: { id: 'a1', name: 'Geese', image_url: null }, raw_score: 70, percentile: 92, rank: 3, fans: 41, is_estimated: false,
  computed_at: '2026-10-25T00:00:00Z' }], journal: { logs: 2, albums_logged: 2, relistens: 0, reviews: 1, ratings: 2, lists: 0, comments: 0 },
  proof: { tickets: 1, merch: 1, screenshots: 0 }, ledger_points: 41.3 };
const bag = { owner: { handle: 'ana', deleted: false }, slots: 5, items: [{ id: 'g1', position: 1, spotify_track_id: '1aaaaaaaaaaaaaaaaaaaaa', title: 'Cowboy Nudes',
  artist: { id: 'a1', name: 'Geese', image_url: null }, artist_status: 'active', album_title: null, cover_url: null,
  spotify_url: 'https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa', added_at: '2026-10-25T00:00:00Z' }] };

function serve(user: object) {
  mockApi.GET.mockImplementation(async (path: string) => {
    switch (path) {
      case '/users/{handle}': return ok(user);
      case '/users/{handle}/logs': return ok({ items: [log], next_cursor: null });
      case '/users/{handle}/answers': return ok(answers);
      case '/users/{handle}/stats': return ok(stats);
      default: return ok(bag);
    }
  });
}

beforeEach(() => jest.clearAllMocks());

test('a stranger sees the basics and is asked to follow; following reveals stats and the grab bag', async () => {
  serve(profile);
  mockApi.PUT.mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
  const openUrl = jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
  render(<FanProfileScreen handle="ana" />);

  expect(await screen.findByText('Ana')).toBeTruthy();
  expect(screen.getByText('@ana · artist · follows you')).toBeTruthy();
  expect(screen.getByText('3 followers · 5 following · 1 log')).toBeTruthy();
  expect(screen.getByText(/Follow Ana to see their fandom stats/)).toBeTruthy();
  expect(screen.getByText('3D Country')).toBeTruthy();
  expect(screen.getByText('“The Velvet Ropes”')).toBeTruthy();
  expect(mockApi.GET).not.toHaveBeenCalledWith('/users/{handle}/stats', expect.anything());
  fireEvent.press(screen.getByRole('link', { name: 'Open Instagram @anaband' }));
  expect(openUrl).toHaveBeenCalledWith('https://instagram.com/anaband');

  serve({ ...profile, followed_by_me: true, can_see_follower_content: true, follower_count: 4 });
  fireEvent.press(screen.getByRole('button', { name: 'Follow @ana' }));
  expect(mockApi.PUT).toHaveBeenCalledWith('/users/{handle}/follow', { params: { path: { handle: 'ana' } } });
  expect(await screen.findByText('Top 8% of fans · #3 of 41')).toBeTruthy();
  expect(screen.getByText('Cowboy Nudes')).toBeTruthy();
  expect(screen.getByText('2 albums logged · 1 review · 2 verified proofs · 41 points')).toBeTruthy();
  await waitFor(() => expect(screen.queryByText(/Follow Ana to see/)).toBeNull());
});

test('your own page offers Edit profile instead of Follow', async () => {
  serve({ ...profile, is_me: true, can_see_follower_content: true, follows_me: false });
  render(<FanProfileScreen handle="ana" />);
  fireEvent.press(await screen.findByRole('button', { name: 'Edit profile' }));
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/profile');
  expect(screen.queryByRole('button', { name: 'Follow @ana' })).toBeNull();
});

test('helpers: band links and standing', () => {
  expect(profileBandLinks({ ...profile, is_artist: false })).toEqual([]);
  expect(profileBandLinks(profile).map((l) => l.label)).toEqual(['Band page', 'Instagram @anaband']);
  expect(standingLine({ ...stats.artists[0]!, percentile: 99.9, is_estimated: true })).toBe('Top 1% of fans · #3 of 41 · estimate');
});
