import { fireEvent, render, screen } from '@testing-library/react-native';

import { StatsScreen } from './StatsScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const problem = (status: number) => ({ error: { code: 'internal_error' }, response: { ok: false, status } });

const mockApi = { GET: jest.fn() };
// Stable across renders, like the real context value.
const mockSession = { api: mockApi };
const me = { handle: 'cat', display_name: 'Cat', is_artist: true, band_url: null, instagram_handle: 'catband', x_handle: null };
const mockState = { status: 'signedIn', me };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: mockState }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn() } }));

const log = { id: 'l1', user: { handle: 'cat', deleted: false }, album: { id: 'al1', title: '3D Country', album_type: 'album', cover_url: null,
  artist: { id: 'a1', name: 'Geese', image_url: null } }, rating: 9, listened_on: '2026-10-20', is_relisten: false, visibility: 'followers', created_at: '2026-10-20T00:00:00Z' };
const answer = { answer: { id: 'pa1', prompt_id: 'p1', user: { handle: 'cat', deleted: false }, body: 'The Velvet Ropes', visibility: 'public',
  created_at: '2026-10-25T00:00:00Z', updated_at: '2026-10-25T00:00:00Z', mine: true, following: false }, prompt_text: 'Best band name?', day: '2026-10-25' };

/** Serves the stats, plus the fan's own logs and answers (empty unless given). */
function serve(stats: unknown, extras: { logs?: unknown[]; answers?: unknown[] } = {}) {
  mockApi.GET.mockImplementation(async (path: string) => {
    switch (path) {
      case '/users/{handle}/logs': return ok({ items: extras.logs ?? [], next_cursor: null });
      case '/users/{handle}/answers': return ok({ items: extras.answers ?? [] });
      default: return ok(stats);
    }
  });
}

const empty = {
  artists: [],
  listening: {
    artists: 0,
    liked_tracks: 0,
    playlist_tracks: 0,
    distinct_albums: 0,
    recent_plays: 0,
    saved_albums: 0,
    top_tracks: 0,
    followed_artists: 0,
    plays_last_quarter: 0,
  },
  journal: { logs: 0, albums_logged: 0, relistens: 0, reviews: 0, ratings: 0, lists: 0, comments: 0 },
  proof: { tickets: 0, merch: 0, screenshots: 0 },
  social: { followers: 0, following: 0 },
  ledger_points: 0,
};

beforeEach(() => jest.clearAllMocks());

test('shows who you are, artist ranks, estimates, every total, and your recent albums and answers', async () => {
  serve(
    {
      ...empty,
      artists: [
        {
          artist: { id: 'a1', name: 'Geese', image_url: null },
          raw_score: 61.4,
          percentile: 84.2,
          rank: 3,
          fans: 18,
          is_estimated: true,
          computed_at: '2026-10-21T12:00:00Z',
        },
      ],
      listening: { ...empty.listening, artists: 1, liked_tracks: 42 },
      journal: { ...empty.journal, albums_logged: 7, reviews: 2 },
      proof: { ...empty.proof, tickets: 1 },
      social: { followers: 12, following: 3 },
      ledger_points: 59.5,
    },
    { logs: [log], answers: [answer] },
  );
  render(<StatsScreen />);

  expect(await screen.findByLabelText('Geese: Top 16% · #3 of 18 fans · score 61 · estimate')).toBeTruthy();
  expect(screen.getByText('Stats')).toBeTruthy();
  expect(screen.getByText('Cat')).toBeTruthy();
  expect(screen.getByText('@cat · artist')).toBeTruthy();
  expect(screen.getByRole('link', { name: 'Open Instagram @catband' })).toBeTruthy();
  expect(screen.getByText(/An estimate means few fans/)).toBeTruthy();
  expect(screen.getByLabelText('Liked songs: 42')).toBeTruthy();
  expect(screen.getByLabelText('Albums logged: 7')).toBeTruthy();
  expect(screen.getByLabelText('Reviews: 2')).toBeTruthy();
  expect(screen.getByLabelText('Tickets: 1')).toBeTruthy();
  expect(screen.getByLabelText('Points earned: 59.50')).toBeTruthy();
  expect(screen.getByLabelText('Followers: 12')).toBeTruthy();
  expect(screen.getByLabelText('Following: 3')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/me/stats');

  // The old public page's content now lives here: recent albums (opening the album) and prompt answers.
  expect(screen.getByText('Geese · ★ 4.5 · followers only')).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Open 3D Country' }));
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/album/al1');
  expect(screen.getByText('Best band name?')).toBeTruthy();
  expect(screen.getByText('“The Velvet Ropes”')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/users/{handle}/logs', { params: { path: { handle: 'cat' }, query: { limit: 5 } } });
});

test('a new fan sees how scores appear, zeros, and no album or answer sections', async () => {
  serve(empty);
  render(<StatsScreen />);

  expect(await screen.findByText(/No scores yet. Connect Spotify/)).toBeTruthy();
  expect(screen.queryByText(/An estimate means/)).toBeNull();
  expect(screen.getByLabelText('Points earned: 0')).toBeTruthy();
  expect(screen.queryByText('Recent albums')).toBeNull();
  expect(screen.queryByText('Prompt answers')).toBeNull();
});

test('a failed load can be retried', async () => {
  mockApi.GET.mockResolvedValueOnce(problem(503));
  render(<StatsScreen />);
  await screen.findByRole('button', { name: 'Try again' });
  serve(empty);

  fireEvent.press(await screen.findByRole('button', { name: 'Try again' }));

  expect(await screen.findByLabelText('Liked songs: 0')).toBeTruthy();
});
