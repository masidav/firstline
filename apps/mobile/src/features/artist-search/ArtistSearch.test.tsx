import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { HomeScreen } from '../home/HomeScreen';

// The artist search lives on the home screen (HomeScreen.tsx); these tests cover its loading, empty, error and retry
// states, and what happens with artists that are already here. HomeScreen.test.tsx covers the rest of the page.

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi, updateMe: jest.fn() };
const mockState = { status: 'signedIn', me: { handle: 'fan_one', spotify: { connected: true } } };

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession, state: mockState }),
}));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn() } }));
jest.mock('../album-journal/journalQueue', () => ({ usePendingLogs: () => [] }));

const geese = {
  id: 'a-1',
  name: 'Geese',
  spotify_artist_id: '066X20Nz7iquqkkCW6Jxy6',
  status: 'active',
  added_by_me: false,
  tracked_at: '2026-10-20T12:00:00Z',
};
const spotifyGeese = { spotify_artist_id: geese.spotify_artist_id, name: 'Geese', genres: ['indie rock'], tracked: false };
const spotifyGoose = { spotify_artist_id: '1aaaaaaaaaaaaaaaaaaaaa', name: 'Goose', genres: ['jam'], tracked: false };

/** Answers every GET: the artists already here for `/artists`, Spotify's results for `/artists/search`, empty pages otherwise. */
function serve(tracked: unknown[], onSpotify: unknown[] = []) {
  mockApi.GET.mockImplementation(async (path: string) => {
    switch (path) {
      case '/artists':
        return ok({ items: tracked, next_cursor: null });
      case '/artists/search':
        return ok({ items: onSpotify });
      default:
        return ok({ items: [], next_cursor: null, unread_count: 0, seed: 'test' });
    }
  });
}

const calls = (path: string) => mockApi.GET.mock.calls.filter(([p]) => p === path);

function search(text: string) {
  const input = screen.getByLabelText('Search artists');
  fireEvent.changeText(input, text);
  fireEvent(input, 'submitEditing');
}

beforeEach(() => jest.clearAllMocks());

test('while Spotify is asked, the screen says so; its answer replaces the note', async () => {
  let answer!: (value: unknown) => void;
  mockApi.GET.mockImplementation((path: string) =>
    path === '/artists/search'
      ? new Promise((resolve) => {
          answer = resolve;
        })
      : Promise.resolve(ok({ items: [], next_cursor: null, unread_count: 0, seed: 'test' })),
  );
  render(<HomeScreen />);

  search('geese');

  expect(await screen.findByText('Looking on Spotify…')).toBeTruthy();
  // No results and no "no match" note while the answer is still on its way.
  expect(screen.queryByText(/No artist here matches/)).toBeNull();
  answer(ok({ items: [spotifyGeese] }));
  expect(await screen.findByRole('button', { name: 'Add Geese' })).toBeTruthy();
  expect(screen.queryByText('Looking on Spotify…')).toBeNull();
});

test('no match here or on Spotify is said in words', async () => {
  serve([], []);
  render(<HomeScreen />);

  fireEvent.changeText(screen.getByLabelText('Search artists'), 'zzz');
  expect(await screen.findByText('No artist here matches "zzz" yet. Press Enter to look on Spotify.')).toBeTruthy();

  fireEvent(screen.getByLabelText('Search artists'), 'submitEditing');
  expect(await screen.findByText('Spotify has no artist matching "zzz".')).toBeTruthy();
  expect(screen.getByRole('header', { name: 'On Spotify' })).toBeTruthy();
  expect(screen.queryByText(/No artist here matches/)).toBeNull();
});

test('a failed search of the artists here is shown, and the next keystroke tries again', async () => {
  mockApi.GET.mockImplementation(async (path: string) =>
    path === '/artists' ? problem(503, 'internal_error') : ok({ items: [], next_cursor: null, unread_count: 0, seed: 'test' }),
  );
  render(<HomeScreen />);

  fireEvent.changeText(screen.getByLabelText('Search artists'), 'gee');
  expect(await screen.findByText('Request failed (503)')).toBeTruthy();
  expect(screen.queryByText(/No artist here matches/)).toBeNull();

  serve([geese]);
  fireEvent.changeText(screen.getByLabelText('Search artists'), 'gees');
  expect(await screen.findByText('Geese')).toBeTruthy();
  expect(screen.queryByText('Request failed (503)')).toBeNull();
});

test('when Spotify is down, Enter again retries', async () => {
  let down = true;
  mockApi.GET.mockImplementation(async (path: string) => {
    if (path === '/artists/search') return down ? problem(503, 'spotify_unavailable') : ok({ items: [spotifyGeese] });
    return ok({ items: [], next_cursor: null, unread_count: 0, seed: 'test' });
  });
  render(<HomeScreen />);

  search('geese');
  expect(await screen.findByText(/Spotify isn't responding/)).toBeTruthy();

  down = false;
  fireEvent(screen.getByLabelText('Search artists'), 'submitEditing');
  expect(await screen.findByRole('button', { name: 'Add Geese' })).toBeTruthy();
  expect(screen.queryByText(/Spotify isn't responding/)).toBeNull();
  expect(calls('/artists/search')).toHaveLength(2);
});

test('an artist already here opens their page from the search list', async () => {
  serve([geese]);
  render(<HomeScreen />);

  fireEvent.changeText(screen.getByLabelText('Search artists'), 'gee');
  fireEvent.press(await screen.findByRole('button', { name: 'Open Geese' }));

  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/artist/a-1/albums');
  expect(screen.queryByText('Getting ready')).toBeNull();
  expect(calls('/artists/search')).toHaveLength(0);
});

test('a Spotify result already tracked here is marked and has no Add button', async () => {
  serve([], [{ ...spotifyGeese, tracked: true, artist_id: geese.id, status: 'active' }, spotifyGoose]);
  render(<HomeScreen />);

  search('gee');

  expect(await screen.findByText('Already here')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Add Geese' })).toBeNull();
  expect(screen.getByRole('button', { name: 'Add Goose' })).toBeTruthy();
  expect(mockApi.POST).not.toHaveBeenCalled();
});

test('while one add is pending, every Add button is off', async () => {
  serve([], [spotifyGeese, spotifyGoose]);
  let finish!: (value: unknown) => void;
  mockApi.POST.mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  render(<HomeScreen />);

  search('goo');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Geese' }));

  await waitFor(() => expect(screen.getByRole('button', { name: 'Add Geese' })).toBeBusy());
  expect(screen.getByRole('button', { name: 'Add Goose' })).toBeDisabled();
  fireEvent.press(screen.getByRole('button', { name: 'Add Goose' }));
  expect(mockApi.POST).toHaveBeenCalledTimes(1);

  finish(ok({ ...geese, status: 'onboarding' }, 201));
  expect(await screen.findByText(/Added Geese/)).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Add Goose' })).toBeEnabled();
});

test('after the daily limit message, Add stays available and the next tap asks again', async () => {
  serve([], [spotifyGeese]);
  mockApi.POST.mockResolvedValueOnce(problem(429, 'artist_add_limit')).mockResolvedValueOnce(ok({ ...geese, status: 'onboarding' }, 201));
  render(<HomeScreen />);

  search('geese');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Geese' }));
  expect(await screen.findByText(/as many new artists as allowed today/)).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Add Geese' }));
  expect(await screen.findByText(/Added Geese/)).toBeTruthy();
  expect(screen.queryByText(/as many new artists as allowed today/)).toBeNull();
  expect(mockApi.POST).toHaveBeenCalledTimes(2);
});

test('clearing the box drops the search results and the Spotify section', async () => {
  serve([geese], [spotifyGoose]);
  render(<HomeScreen />);

  search('goose');
  expect(await screen.findByRole('header', { name: 'On Spotify' })).toBeTruthy();

  fireEvent.changeText(screen.getByLabelText('Search artists'), '');
  await waitFor(() => expect(screen.queryByRole('header', { name: 'On Spotify' })).toBeNull());
  expect(screen.queryByText('Goose')).toBeNull();
  expect(screen.getByRole('button', { name: 'Post a photo' })).toBeTruthy();
});
