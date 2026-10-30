import { fireEvent, render, screen } from '@testing-library/react-native';

import { StatsScreen } from './StatsScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const problem = (status: number) => ({ error: { code: 'internal_error' }, response: { ok: false, status } });

const mockApi = { GET: jest.fn() };
// Stable across renders, like the real context value.
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn() } }));

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

test('shows artist ranks, labels estimates, and lists every total', async () => {
  mockApi.GET.mockResolvedValue(
    ok({
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
    }),
  );
  render(<StatsScreen />);

  expect(await screen.findByLabelText('Geese: Top 16% · #3 of 18 fans · score 61 · estimate')).toBeTruthy();
  expect(screen.getByText(/An estimate means few fans/)).toBeTruthy();
  expect(screen.getByLabelText('Liked songs: 42')).toBeTruthy();
  expect(screen.getByLabelText('Albums logged: 7')).toBeTruthy();
  expect(screen.getByLabelText('Reviews: 2')).toBeTruthy();
  expect(screen.getByLabelText('Tickets: 1')).toBeTruthy();
  expect(screen.getByLabelText('Points earned: 59.50')).toBeTruthy();
  expect(screen.getByLabelText('Followers: 12')).toBeTruthy();
  expect(screen.getByLabelText('Following: 3')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/me/stats');
});

test('a new fan sees how scores appear, and zeros', async () => {
  mockApi.GET.mockResolvedValue(ok(empty));
  render(<StatsScreen />);

  expect(await screen.findByText(/No scores yet. Connect Spotify/)).toBeTruthy();
  expect(screen.queryByText(/An estimate means/)).toBeNull();
  expect(screen.getByLabelText('Points earned: 0')).toBeTruthy();
});

test('a failed load can be retried', async () => {
  mockApi.GET.mockResolvedValueOnce(problem(503)).mockResolvedValueOnce(ok(empty));
  render(<StatsScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Try again' }));

  expect(await screen.findByLabelText('Liked songs: 0')).toBeTruthy();
});
