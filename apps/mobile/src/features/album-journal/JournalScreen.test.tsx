import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { JournalScreen } from './JournalScreen';
import { journalQueue } from './journalQueue';
import type { PendingLog } from './pendingLogs';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn' } }) }));
jest.mock('expo-router', () => ({ useFocusEffect: (effect: () => void) => jest.requireActual('react').useEffect(effect, [effect]), router: { push: jest.fn(), replace: jest.fn() } }));
jest.mock('expo-crypto', () => ({ randomUUID: () => 'key-1' }));
jest.mock('./queueStore', () => ({ queueStore: jest.requireActual('./pendingLogs').memoryQueueStore() }));

const entry = (id: string, title: string, extra: Record<string, unknown> = {}) => ({
  id,
  user: { handle: 'fan_one', deleted: false },
  album: { id: `al-${id}`, title, album_type: 'album', cover_url: null, artist: { id: 'ar-1', name: 'Radiohead' } },
  rating: 7,
  review: null,
  listened_on: '2026-10-21',
  is_relisten: false,
  visibility: 'public',
  comment_count: 0,
  created_at: '2026-10-21T12:00:00Z',
  updated_at: '2026-10-21T12:00:00Z',
  ...extra,
});

beforeEach(() => jest.clearAllMocks());

test('lists my logs and deletes one', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [entry('1', 'In Rainbows', { is_relisten: true }), entry('2', 'The Bends')], next_cursor: null }));
  mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
  render(<JournalScreen />);

  expect(await screen.findByText('In Rainbows')).toBeTruthy();
  expect(screen.getByText('Radiohead · 2026-10-21 · relisten')).toBeTruthy();
  expect(screen.getAllByText('3.5 ★')).toHaveLength(2);

  await act(async () => fireEvent.press(screen.getByLabelText('Delete log of The Bends')));
  await waitFor(() => expect(screen.queryByText('The Bends')).toBeNull());
  expect(mockApi.DELETE).toHaveBeenCalledWith('/logs/{logId}', { params: { path: { logId: '2' } } });

  fireEvent.press(screen.getByLabelText('Open In Rainbows'));
  const { router } = jest.requireMock('expo-router');
  expect(router.push).toHaveBeenCalledWith('/album/al-1');
});

test('an empty journal explains what to do', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [], next_cursor: null }));
  render(<JournalScreen />);

  expect(await screen.findByText(/Nothing logged yet/)).toBeTruthy();
});

test('entries waiting to sync are listed first, with their reason when refused, and can be retried', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [entry('1', 'In Rainbows')], next_cursor: null }));
  const mockPost = jest.fn().mockResolvedValueOnce({ error: { code: 'not_found' }, response: { ok: false, status: 404 } });
  await journalQueue.start({ ...mockApi, POST: mockPost } as never, 'user-1');
  await journalQueue.add({
    userId: 'user-1',
    kind: 'create',
    albumId: 'al-9',
    album: { title: 'Kid A', artistName: 'Radiohead', coverUrl: null },
    body: { rating: 10, review: 'Still ahead of its time.', listened_on: '2026-10-26', visibility: 'followers' },
  });
  await journalQueue.flush();
  render(<JournalScreen />);

  expect(await screen.findByText('1 entry waiting to sync')).toBeTruthy();
  expect(screen.getByText('Kid A · Radiohead')).toBeTruthy();
  expect(screen.getByText('5 ★ · 2026-10-26 · followers')).toBeTruthy();
  expect(screen.getByText("Couldn't sync: This album or log is no longer on Fandom Score.")).toBeTruthy();

  mockPost.mockResolvedValueOnce(ok({ log: entry('9', 'Kid A'), points_awarded: [] }, 201));
  await act(async () => fireEvent.press(screen.getByRole('button', { name: 'Retry now' })));

  await waitFor(() => expect(screen.queryByText(/waiting to sync/)).toBeNull());
  expect(mockPost).toHaveBeenCalledTimes(2);
  // The journal reloads so the new log shows in its place.
  expect(mockApi.GET).toHaveBeenCalledTimes(2);
  const pending: PendingLog[] = journalQueue.getPending();
  expect(pending).toEqual([]);
});
