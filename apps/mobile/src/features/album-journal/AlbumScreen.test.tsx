import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { AlbumScreen } from './AlbumScreen';
import { journalQueue } from './journalQueue';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), PATCH: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { id: 'user-1' } } }),
}));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false } }));
let mockKeys = 0;
jest.mock('expo-crypto', () => ({ randomUUID: () => `key-${++mockKeys}` }));
// The queue keeps entries in memory for these tests (no file or localStorage).
jest.mock('./queueStore', () => ({ queueStore: jest.requireActual('./pendingLogs').memoryQueueStore() }));

const artist = { id: 'ar-1', name: 'Radiohead', image_url: null };
const album = {
  id: 'al-1',
  artist,
  title: 'In Rainbows',
  album_type: 'album',
  release_date: '2007-10-10',
  release_date_precision: 'day',
  cover_url: 'https://i.scdn.co/al-1',
  spotify_album_id: 'sp-al-1',
  delisted: false,
  community: { log_count: 0, average_rating: null },
  tracks: [
    { id: 't-1', title: '15 Step', track_number: 1, disc_number: 1, spotify_track_id: 'sp-t-1' },
    { id: 't-2', title: 'Bodysnatchers', track_number: 2, disc_number: 1, spotify_track_id: 'sp-t-2' },
  ],
  my_latest_log: null,
};
const log = (overrides: Record<string, unknown> = {}) => ({
  id: 'log-1',
  user: { handle: 'fan_one', deleted: false },
  album: { id: 'al-1', title: 'In Rainbows', album_type: 'album', cover_url: album.cover_url, artist },
  rating: 9,
  review: null,
  listened_on: '2026-10-21',
  is_relisten: false,
  visibility: 'public',
  comment_count: 0,
  created_at: '2026-10-21T12:00:00Z',
  updated_at: '2026-10-21T12:00:00Z',
  ...overrides,
});

function serve(detail: unknown, logs: unknown[] = []) {
  mockApi.GET.mockImplementation(async (path: string) =>
    path === '/albums/{albumId}' ? ok(detail) : ok({ items: logs, next_cursor: null }),
  );
}

beforeEach(async () => {
  jest.clearAllMocks();
  mockKeys = 0;
  // Signed in as user-1 with an empty queue, as the root layout's JournalSync would have it.
  await journalQueue.start(mockApi as never, 'user-1');
  for (const entry of journalQueue.getPending()) await journalQueue.discard(entry.localId);
});
afterAll(() => journalQueue.stop());

test('shows the album, its tracks, and logs it with a rating and review', async () => {
  serve(album, [log({ id: 'log-9', user: { handle: 'other_fan', deleted: false }, rating: 6, review: 'Fine.' })]);
  mockApi.POST.mockResolvedValue(
    ok({ log: log(), points_awarded: [{ rule_key: 'album_logged', points: 3 }, { rule_key: 'review_written', points: 4 }] }, 201),
  );
  render(<AlbumScreen albumId="al-1" />);

  expect(await screen.findByText('In Rainbows')).toBeTruthy();
  expect(screen.getByText('2007 · Album')).toBeTruthy();
  expect(screen.getByText('Not logged by anyone yet')).toBeTruthy();
  expect(screen.getByText('1. 15 Step')).toBeTruthy();
  expect(screen.getByText('@other_fan · 3 ★ · 2026-10-21')).toBeTruthy();
  expect(screen.getByLabelText('Open on Spotify')).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Log this album' }));
  fireEvent.press(screen.getByLabelText('Rate 4.5 stars'));
  fireEvent.changeText(screen.getByLabelText('Review'), 'A warm record.');
  fireEvent.changeText(screen.getByLabelText('Listened on'), '2026-10-20');
  fireEvent.press(screen.getByLabelText('Visibility Private'));
  fireEvent.press(screen.getByRole('button', { name: 'Save log' }));

  expect(await screen.findByText('You earned 7 points: first log, review.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/logs', {
    headers: { 'Idempotency-Key': 'key-1' },
    body: { album_id: 'al-1', rating: 9, review: 'A warm record.', listened_on: '2026-10-20', visibility: 'private' },
  });
  // The album is reloaded so community counts and "your log" update.
  await waitFor(() => expect(mockApi.GET.mock.calls.filter(([p]) => p === '/albums/{albumId}').length).toBe(2));
});

test('an existing log can be edited, and logging again is a relisten', async () => {
  serve({ ...album, my_latest_log: log({ rating: 8, review: 'Great.' }) });
  mockApi.PATCH.mockResolvedValue(ok({ log: log({ rating: 10, review: 'Great.' }), points_awarded: [] }));
  mockApi.POST.mockResolvedValue(ok({ log: log({ id: 'log-2', is_relisten: true }), points_awarded: [] }, 201));
  render(<AlbumScreen albumId="al-1" />);

  expect(await screen.findByText('4 ★ · 2026-10-21 · public')).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Edit' }));
  fireEvent.press(screen.getByLabelText('Rate 5 stars'));
  fireEvent.press(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() =>
    expect(mockApi.PATCH).toHaveBeenCalledWith('/logs/{logId}', {
      params: { path: { logId: 'log-1' } },
      body: { rating: 10, review: 'Great.', listened_on: '2026-10-21', visibility: 'public' },
    }),
  );
  expect(await screen.findByText('Saved.')).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Log again' }));
  fireEvent.press(screen.getByRole('button', { name: 'Save log' }));
  expect(await screen.findByText('Relisten saved.')).toBeTruthy();
  expect(mockApi.POST.mock.calls[0]?.[1].body.album_id).toBe('al-1');
});

test('a delisted album cannot be logged and shows no Spotify link', async () => {
  serve({ ...album, delisted: true, cover_url: null, spotify_album_id: null });
  render(<AlbumScreen albumId="al-1" />);

  expect(await screen.findByText(/Spotify no longer lists this release/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Log this album' })).toBeNull();
  expect(screen.queryByLabelText('Open on Spotify')).toBeNull();
});

test('without a connection the log is kept on the device, then sent when the connection is back', async () => {
  serve(album);
  mockApi.POST.mockRejectedValue(new TypeError('Network request failed'));
  render(<AlbumScreen albumId="al-1" />);

  fireEvent.press(await screen.findByRole('button', { name: 'Log this album' }));
  fireEvent.press(screen.getByLabelText('Rate 4 stars'));
  fireEvent.changeText(screen.getByLabelText('Review'), 'Logged on the train.');
  fireEvent.press(screen.getByRole('button', { name: 'Save log' }));

  expect(await screen.findByText("Saved on this device. It will sync when you're back online.")).toBeTruthy();
  expect(screen.getByText('4 ★ · ' + new Date().toISOString().slice(0, 10) + ' · public')).toBeTruthy();
  expect(screen.getByText('Logged on the train.')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Log this album' })).toBeTruthy();
  // The form's attempt, then the queue's first try (also offline).
  expect(mockApi.POST).toHaveBeenCalledTimes(2);

  // Back online: the queue sends it with the same Idempotency-Key as the first attempt; the screen shows the points.
  mockApi.POST.mockResolvedValue(ok({ log: log({ rating: 8 }), points_awarded: [{ rule_key: 'album_logged', points: 3 }] }, 201));
  await act(async () => {
    await journalQueue.flush();
  });

  expect(await screen.findByText('You earned 3 points: first log.')).toBeTruthy();
  expect(screen.queryByText(/Saved on this device/)).toBeNull();
  expect(mockApi.POST).toHaveBeenCalledTimes(3);
  for (const call of mockApi.POST.mock.calls) {
    expect(call[1].headers).toEqual({ 'Idempotency-Key': 'key-1' });
    expect(call[1].body.review).toBe('Logged on the train.');
  }
});

test('a waiting entry can be edited and discarded without touching the API', async () => {
  serve(album);
  mockApi.POST.mockRejectedValue(new TypeError('Network request failed'));
  render(<AlbumScreen albumId="al-1" />);

  fireEvent.press(await screen.findByRole('button', { name: 'Log this album' }));
  fireEvent.press(screen.getByLabelText('Rate 2 stars'));
  fireEvent.press(screen.getByRole('button', { name: 'Save log' }));
  await screen.findByText(/Saved on this device/);

  fireEvent.press(screen.getByLabelText('Edit pending log of In Rainbows'));
  fireEvent.press(screen.getByLabelText('Rate 5 stars'));
  fireEvent.press(screen.getByRole('button', { name: 'Save changes' }));
  expect(await screen.findByText(/^5 ★ · /)).toBeTruthy();
  expect(journalQueue.getPending()[0]?.body.rating).toBe(10);

  fireEvent.press(screen.getByLabelText('Discard pending log of In Rainbows'));
  await waitFor(() => expect(screen.queryByText(/Saved on this device/)).toBeNull());
  expect(journalQueue.getPending()).toEqual([]);
  expect(mockApi.PATCH).not.toHaveBeenCalled();
});
