import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { HomeScreen } from './HomeScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn() };
// Stable across renders, like the real context value.
const mockSession = { api: mockApi };

const mockState = { status: 'signedIn', me: { spotify: { connected: true } } };

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession, state: mockState }),
}));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn() } }));

const geese = {
  id: 'a-1',
  name: 'Geese',
  spotify_artist_id: '066X20Nz7iquqkkCW6Jxy6',
  status: 'onboarding',
  added_by_me: true,
  tracked_at: '2026-10-20T12:00:00Z',
};
const spotifyGeese = { spotify_artist_id: geese.spotify_artist_id, name: 'Geese', genres: ['indie rock'], tracked: false };

/**
 * Answers GET /artists with `tracked` (or `onboarding` when asked for the fan's own onboarding artists),
 * GET /artists/search with `onSpotify`, and GET /me/scores with `scores`.
 */
function serve(tracked: unknown[], onSpotify: unknown[] = [], scores: unknown[] = [], onboarding: unknown[] = []) {
  mockApi.GET.mockImplementation(async (path: string, options?: { params?: { query?: { added_by_me?: boolean } } }) => {
    switch (path) {
      case '/artists':
        return ok({ items: options?.params?.query?.added_by_me ? onboarding : tracked, next_cursor: null });
      case '/me/scores':
        return ok({ items: scores, next_cursor: null });
      default:
        return ok({ items: onSpotify });
    }
  });
}

const calls = (path: string) => mockApi.GET.mock.calls.filter(([p]) => p === path);

beforeEach(() => jest.clearAllMocks());

const score = (name: string, percentile: number, isEstimated: boolean) => ({
  artist_id: `id-${name}`,
  artist: { id: `id-${name}`, name, image_url: null },
  percentile,
  raw_score: 61.4,
  is_estimated: isEstimated,
  computed_at: '2026-10-21T12:00:00Z',
});

test('with an empty search box, your artists show with their scores', async () => {
  serve([], [], [score('Geese', 84.2, true), score('Radiohead', 40, false)]);
  render(<HomeScreen />);

  expect(await screen.findByText('Geese')).toBeTruthy();
  expect(screen.getByText('Top 16% of fans · score 61 · estimate')).toBeTruthy();
  expect(screen.getByText('Top 60% of fans · score 61')).toBeTruthy();
  expect(screen.getByText(/An estimate means few fans/)).toBeTruthy();
  expect(calls('/me/scores')[0]?.[1]).toEqual({ params: { query: { sort: 'percentile', limit: 100 } } });

  // Typing replaces them with search results.
  fireEvent.changeText(screen.getByLabelText('Search artists'), 'zz');
  await waitFor(() => expect(screen.queryByText('Radiohead')).toBeNull());
});

test('artists a sync added from your top artists show while they onboard', async () => {
  serve([], [], [score('Radiohead', 40, false)], [geese]);
  render(<HomeScreen />);

  expect(await screen.findByText('Geese')).toBeTruthy();
  expect(screen.getByText('Setting up · your score comes with the next sync')).toBeTruthy();
  expect(screen.getByText('Radiohead')).toBeTruthy();
  expect(calls('/artists').map(([, options]) => options)).toContainEqual({
    params: { query: { added_by_me: true, status: 'onboarding', sort: 'recent', limit: 100 } },
  });
  expect(screen.queryByText(/None of the artists you listen to/)).toBeNull();
});

test('with no scores yet, a fan without Spotify is asked to connect it', async () => {
  mockState.me.spotify.connected = false;
  serve([]);
  render(<HomeScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Connect Spotify' }));

  const { router } = jest.requireMock('expo-router');
  expect(router.push).toHaveBeenCalledWith('/spotify');
  mockState.me.spotify.connected = true;
});

test('with no scores yet, a connected fan is told how scores appear', async () => {
  serve([]);
  render(<HomeScreen />);

  expect(await screen.findByText(/None of the artists you listen to are here yet/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Connect Spotify' })).toBeNull();
});

test('a failed load of your artists can be retried', async () => {
  mockApi.GET.mockResolvedValueOnce(problem(503, 'internal_error'));
  render(<HomeScreen />);

  const retry = await screen.findByRole('button', { name: 'Try again' });
  serve([], [], [score('Geese', 50, false)]);
  fireEvent.press(retry);

  expect(await screen.findByText('Geese')).toBeTruthy();
});

test('typing searches the artists already here, not Spotify', async () => {
  serve([geese]);
  render(<HomeScreen />);

  fireEvent.changeText(screen.getByLabelText('Search artists'), 'gee');

  expect(await screen.findByText('Geese')).toBeTruthy();
  expect(screen.getByText('Getting ready')).toBeTruthy();
  expect(calls('/artists').map(([, options]) => options)).toContainEqual({
    params: { query: { q: 'gee', status: 'all', sort: 'name', limit: 20 } },
  });
  expect(calls('/artists/search')).toHaveLength(0);
});

test('Enter on an exact match does not ask Spotify', async () => {
  serve([geese]);
  render(<HomeScreen />);

  const input = screen.getByLabelText('Search artists');
  fireEvent.changeText(input, 'geese');
  fireEvent(input, 'submitEditing');

  expect(await screen.findByText('Geese')).toBeTruthy();
  expect(calls('/artists/search')).toHaveLength(0);
});

test('Enter with no match asks Spotify, and Add tracks the artist', async () => {
  serve([], [spotifyGeese]);
  mockApi.POST.mockResolvedValue(ok(geese, 201));
  render(<HomeScreen />);

  const input = screen.getByLabelText('Search artists');
  fireEvent.changeText(input, 'geese');
  fireEvent(input, 'submitEditing');

  const addButton = await screen.findByRole('button', { name: 'Add Geese' });
  expect(screen.getByText('indie rock')).toBeTruthy();
  serve([geese], [spotifyGeese]);
  fireEvent.press(addButton);

  expect(await screen.findByText(/Added Geese/)).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/artists', { body: { spotify_artist_id: geese.spotify_artist_id } });
  expect(screen.getByText('Already here')).toBeTruthy();
  await waitFor(() => expect(screen.getByText('Getting ready')).toBeTruthy());
});

test('same-named artists show their release counts', async () => {
  serve(
    [],
    [
      { ...spotifyGeese, genres: [], release_count: 59 },
      { ...spotifyGeese, spotify_artist_id: '0bbbbbbbbbbbbbbbbbbbbb', genres: [], release_count: 1 },
    ],
  );
  render(<HomeScreen />);

  const input = screen.getByLabelText('Search artists');
  fireEvent.changeText(input, 'geese');
  fireEvent(input, 'submitEditing');

  expect(await screen.findByText('59 releases')).toBeTruthy();
  expect(screen.getByText('1 release')).toBeTruthy();
  // Each button shows "Add"; screen readers hear the artist's name too.
  expect(screen.getAllByText('Add')).toHaveLength(2);
  expect(screen.getAllByRole('button', { name: 'Add Geese' })).toHaveLength(2);
});

test('the daily add limit is explained', async () => {
  serve([], [spotifyGeese]);
  mockApi.POST.mockResolvedValue(problem(429, 'artist_add_limit'));
  render(<HomeScreen />);

  const input = screen.getByLabelText('Search artists');
  fireEvent.changeText(input, 'geese');
  fireEvent(input, 'submitEditing');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Geese' }));

  expect(await screen.findByText(/as many new artists as allowed today/)).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Add Geese' })).toBeTruthy();
});

test('Spotify being down is shown, and nothing is added', async () => {
  mockApi.GET.mockImplementation(async (path: string) =>
    path === '/artists/search' ? problem(503, 'spotify_unavailable') : ok({ items: [], next_cursor: null }),
  );
  render(<HomeScreen />);

  const input = screen.getByLabelText('Search artists');
  fireEvent.changeText(input, 'geese');
  fireEvent(input, 'submitEditing');

  expect(await screen.findByText(/Spotify isn't responding/)).toBeTruthy();
  expect(mockApi.POST).not.toHaveBeenCalled();
});

test('one letter is too short for Spotify', async () => {
  serve([]);
  render(<HomeScreen />);

  const input = screen.getByLabelText('Search artists');
  fireEvent.changeText(input, 'g');
  fireEvent(input, 'submitEditing');

  expect(await screen.findByText(/at least 2 letters/)).toBeTruthy();
  expect(calls('/artists/search')).toHaveLength(0);
});
