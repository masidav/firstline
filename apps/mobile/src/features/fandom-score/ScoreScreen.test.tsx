import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { ScoreScreen } from './ScoreScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn' } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false } }));
jest.mock('../sharing/shareCard', () => ({ shareScoreCard: jest.fn() }));
const mockShare = jest.requireMock('../sharing/shareCard').shareScoreCard as jest.Mock;

const artist = { id: 'ar-1', name: 'Geese', spotify_artist_id: 'sp-geese', status: 'active', tracked_at: '2026-10-20T12:00:00Z', image_url: null };

const ready = {
  artist_id: 'ar-1',
  status: 'ready',
  percentile: 84.2,
  raw_score: 23.4,
  is_estimated: true,
  baseline_weight: 0.985,
  computed_at: '2026-10-24T12:00:00Z',
  breakdown: {
    spotify: {
      liked_tracks: 12,
      top_artist_rank: 3,
      followed: true,
      catalog_releases: 5,
      distinct_albums: 2,
      shares: { liked: 0.5, top_artist: 0.96, follow: 1, album_depth: 0.4 },
      points: 23.4,
    },
    ledger_points: 5,
    total: 28.4,
  },
};

/** GET /artists/{id} answers with the artist and GET .../score with `score`. */
function serve(score: unknown) {
  mockApi.GET.mockImplementation(async (path: string) => (path.endsWith('/score') ? ok(score) : ok(artist)));
}

beforeEach(() => jest.clearAllMocks());

test('a ready score shows the percentile, the estimate label, and the signals behind it', async () => {
  serve(ready);
  render(<ScoreScreen artistId="ar-1" />);

  expect(await screen.findByRole('header', { name: 'Your Geese score' })).toBeTruthy();
  expect(screen.getByRole('header', { name: 'Top 16% of fans' })).toBeTruthy();
  expect(screen.getByText('Score 23 · percentile 84 · estimate')).toBeTruthy();
  expect(screen.getByText(/Estimate: few fans of this artist are here yet/)).toBeTruthy();
  expect(screen.getByText(/not among every fan in the world/)).toBeTruthy();
  expect(screen.getByLabelText('Liked songs: 12 songs, 50% of full marks')).toBeTruthy();
  expect(screen.getByLabelText('Top artist, last 6 months: #3, 96% of full marks')).toBeTruthy();
  expect(screen.getByLabelText('Following on Spotify: Yes, 100% of full marks')).toBeTruthy();
  expect(screen.getByText('Spotify 23 + journal and proof 5 = total 28')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/artists/{artistId}/score', { params: { path: { artistId: 'ar-1' } } });

  fireEvent.press(screen.getByRole('button', { name: 'Albums' }));
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/artist/ar-1/albums');
});

test('a ready score can be shared as a card, and a web download says so', async () => {
  serve(ready);
  mockShare.mockResolvedValueOnce('shared').mockResolvedValueOnce('downloaded');
  render(<ScoreScreen artistId="ar-1" />);

  fireEvent.press(await screen.findByRole('button', { name: 'Share my score card' }));
  await waitFor(() => expect(mockShare).toHaveBeenCalledWith(mockApi, 'ar-1'));
  fireEvent.press(screen.getByRole('button', { name: 'Share my score card' }));
  expect(await screen.findByText('Saved your score card as an image.')).toBeTruthy();
});

test('a final score has no estimate label', async () => {
  serve({ ...ready, is_estimated: false, baseline_weight: 0.2 });
  render(<ScoreScreen artistId="ar-1" />);

  expect(await screen.findByText('Score 23 · percentile 84')).toBeTruthy();
  expect(screen.queryByText(/Estimate:/)).toBeNull();
});

test('an onboarding artist explains when the score arrives', async () => {
  serve({ artist_id: 'ar-1', status: 'artist_onboarding', is_estimated: false });
  render(<ScoreScreen artistId="ar-1" />);

  expect(await screen.findByText(/still being set up/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Refresh' })).toBeNull();
});

test('a computing score offers a refresh that queues a recompute and reloads', async () => {
  serve({ artist_id: 'ar-1', status: 'computing', is_estimated: false });
  mockApi.POST.mockResolvedValue(ok({ artist_id: 'ar-1', status: 'computing' }, 202));
  render(<ScoreScreen artistId="ar-1" />);

  const refresh = await screen.findByRole('button', { name: 'Refresh' });
  expect(screen.getByText(/Computing your score/)).toBeTruthy();
  serve(ready);
  fireEvent.press(refresh);

  expect(mockApi.POST).toHaveBeenCalledWith('/artists/{artistId}/score/refresh', { params: { path: { artistId: 'ar-1' } } });
  expect(await screen.findByRole('header', { name: 'Top 16% of fans' })).toBeTruthy();
});

test('a paused or unknown artist is reported, not errored', async () => {
  mockApi.GET.mockResolvedValue(problem(404, 'not_found'));
  render(<ScoreScreen artistId="ar-9" />);

  expect(await screen.findByText(/isn't on the tracked list any more/)).toBeTruthy();
});

test('a failed load can be retried', async () => {
  mockApi.GET.mockResolvedValueOnce(problem(503, 'internal_error')).mockResolvedValueOnce(problem(503, 'internal_error'));
  render(<ScoreScreen artistId="ar-1" />);

  const retry = await screen.findByRole('button', { name: 'Try again' });
  serve(ready);
  fireEvent.press(retry);

  await waitFor(() => expect(screen.getByRole('header', { name: 'Top 16% of fans' })).toBeTruthy());
});
