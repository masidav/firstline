/**
 * @jest-environment node
 */
import { ApiError } from '../../api/client';
import { JournalQueue } from './journalQueue';
import { memoryQueueStore, type PendingLog } from './pendingLogs';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, body: Record<string, unknown>) => ({ error: body, response: { ok: false, status } });

const mockApi = { POST: jest.fn(), PATCH: jest.fn(), GET: jest.fn(), DELETE: jest.fn() };
const api = mockApi as never;
const album = { title: 'In Rainbows', artistName: 'Radiohead', coverUrl: null };
const body = { rating: 9, review: null, listened_on: '2026-10-26', visibility: 'public' as const };
const serverLog = { id: 'log-1', album: { id: 'al-1', title: 'In Rainbows' }, listened_on: '2026-10-26' };

let ids = 0;
const queue = (store = memoryQueueStore()) => new JournalQueue(store, () => `id-${++ids}`);

beforeEach(() => {
  jest.clearAllMocks();
  jest.useFakeTimers();
  ids = 0;
});
afterEach(() => jest.useRealTimers());

test('an entry added while signed in is sent at once with its Idempotency-Key, then removed', async () => {
  const store = memoryQueueStore();
  const q = queue(store);
  const synced = jest.fn();
  q.onSynced(synced);
  mockApi.POST.mockResolvedValue(ok({ log: serverLog, points_awarded: [{ rule_key: 'album_logged', points: 3 }] }, 201));
  await q.start(api, 'user-1');

  const entry = await q.add({ userId: 'user-1', kind: 'create', albumId: 'al-1', album, body });
  await q.flush();

  expect(mockApi.POST).toHaveBeenCalledWith('/logs', {
    headers: { 'Idempotency-Key': entry.key },
    body: { album_id: 'al-1', rating: 9, listened_on: '2026-10-26', visibility: 'public' },
  });
  expect(q.getPending()).toEqual([]);
  expect(store.items).toEqual([]);
  expect(synced).toHaveBeenCalledWith(expect.objectContaining({ localId: entry.localId }), expect.objectContaining({ log: serverLog }));
});

test('offline, the entry waits, is retried with the same key after a delay, and is kept on disk meanwhile', async () => {
  const store = memoryQueueStore();
  const q = queue(store);
  mockApi.POST.mockRejectedValueOnce(new TypeError('Network request failed')).mockResolvedValueOnce(ok({ log: serverLog, points_awarded: [] }, 201));
  await q.start(api, 'user-1');

  await q.add({ userId: 'user-1', kind: 'create', albumId: 'al-1', album, body });
  await q.flush();

  expect(q.getPending()).toHaveLength(1);
  expect(q.getPending()[0]?.attempts).toBe(1);
  expect(q.getPending()[0]?.failure).toBeUndefined();
  expect(store.items).toHaveLength(1);

  await jest.advanceTimersByTimeAsync(5000);
  await q.flush();

  expect(mockApi.POST).toHaveBeenCalledTimes(2);
  expect(mockApi.POST.mock.calls[1][1].headers).toEqual(mockApi.POST.mock.calls[0][1].headers);
  expect(q.getPending()).toEqual([]);
});

test('an entry the API refuses stays with its reason and is not retried on its own; the next entry still goes', async () => {
  const q = queue();
  mockApi.POST.mockImplementation(async (_path: string, { body: b }: { body: { album_id: string } }) =>
    b.album_id === 'gone'
      ? problem(404, { code: 'not_found' })
      : ok({ log: serverLog, points_awarded: [] }, 201),
  );
  await q.start(api, 'user-1');

  await q.add({ userId: 'user-1', kind: 'create', albumId: 'gone', album, body });
  await q.add({ userId: 'user-1', kind: 'create', albumId: 'al-1', album, body });
  await q.flush();

  expect(q.getPending()).toHaveLength(1);
  expect(q.getPending()[0]).toMatchObject({ albumId: 'gone', failure: 'This album or log is no longer on Fandom Score.' });
  expect(jest.getTimerCount()).toBe(0);

  // Retry now clears the failure and tries again.
  mockApi.POST.mockResolvedValue(ok({ log: serverLog, points_awarded: [] }, 201));
  await q.retry();
  expect(q.getPending()).toEqual([]);
});

test('a validation error names the field; editing the entry sends it again', async () => {
  const q = queue();
  mockApi.POST
    .mockResolvedValueOnce(problem(422, { code: 'validation_failed', errors: [{ field: 'listened_on', message: 'Cannot be in the future.' }] }))
    .mockResolvedValueOnce(ok({ log: serverLog, points_awarded: [] }, 201));
  await q.start(api, 'user-1');

  const entry = await q.add({ userId: 'user-1', kind: 'create', albumId: 'al-1', album, body: { ...body, listened_on: '2030-01-01' } });
  await q.flush();
  expect(q.getPending()[0]?.failure).toBe('Listened on: Cannot be in the future.');

  await q.edit(entry.localId, body);
  await q.flush();
  expect(q.getPending()).toEqual([]);
  expect(mockApi.POST.mock.calls[1][1].body.listened_on).toBe('2026-10-26');
});

test('only the signed-in account’s entries are sent or shown; the rest wait for their owner', async () => {
  const theirs: PendingLog = {
    localId: 'theirs', userId: 'user-2', key: 'k-2', kind: 'create', albumId: 'al-1', album, body, createdAt: '2026-10-25T00:00:00Z', attempts: 0,
  };
  const store = memoryQueueStore([theirs]);
  const q = queue(store);
  mockApi.POST.mockResolvedValue(ok({ log: serverLog, points_awarded: [] }, 201));

  await q.start(api, 'user-1');
  expect(q.getPending()).toEqual([]);
  await q.add({ userId: 'user-1', kind: 'create', albumId: 'al-2', album, body });
  await q.flush();

  expect(mockApi.POST).toHaveBeenCalledTimes(1);
  expect(mockApi.POST.mock.calls[0][1].body.album_id).toBe('al-2');
  expect(q.getPending()).toEqual([]);
  expect(store.items.map((item: PendingLog) => item.userId)).toEqual(['user-2']);
});

test('an edit of a server log goes out as PATCH; a second edit of the same log replaces the first', async () => {
  const q = queue();
  mockApi.PATCH.mockRejectedValueOnce(new TypeError('Network request failed')).mockResolvedValue(ok({ log: serverLog, points_awarded: [] }));
  await q.start(api, 'user-1');

  await q.add({ userId: 'user-1', kind: 'update', logId: 'log-1', albumId: 'al-1', album, body });
  await q.flush();
  await q.add({ userId: 'user-1', kind: 'update', logId: 'log-1', albumId: 'al-1', album, body: { ...body, rating: 10 } });
  await q.flush();

  expect(q.getPending()).toEqual([]);
  expect(mockApi.PATCH).toHaveBeenLastCalledWith('/logs/{logId}', {
    params: { path: { logId: 'log-1' } },
    body: { rating: 10, review: null, listened_on: '2026-10-26', visibility: 'public' },
  });
});

test('a session that ended stops the sync without touching the entries; discard removes one', async () => {
  const q = queue();
  mockApi.POST.mockResolvedValue(problem(401, { code: 'unauthorized' }));
  await q.start(api, 'user-1');

  const entry = await q.add({ userId: 'user-1', kind: 'create', albumId: 'al-1', album, body });
  await q.flush();
  expect(q.getPending()[0]?.attempts).toBe(0);
  expect(q.getPending()[0]?.failure).toBeUndefined();

  await q.discard(entry.localId);
  expect(q.getPending()).toEqual([]);
});

test('entries saved by an earlier launch are loaded and sent on start', async () => {
  const stored: PendingLog = {
    localId: 'old', userId: 'user-1', key: 'k-old', kind: 'create', albumId: 'al-1', album, body, createdAt: '2026-10-25T00:00:00Z', attempts: 2,
  };
  const q = queue(memoryQueueStore([stored]));
  mockApi.POST.mockResolvedValue(ok({ log: serverLog, points_awarded: [] }, 201));

  await q.start(api, 'user-1');
  await q.flush();

  expect(mockApi.POST.mock.calls[0][1].headers).toEqual({ 'Idempotency-Key': 'k-old' });
  expect(q.getPending()).toEqual([]);
});

test('a server error is retried later, not treated as a refusal', async () => {
  const q = queue();
  mockApi.POST.mockResolvedValue(problem(503, { code: 'unavailable' }));
  await q.start(api, 'user-1');

  await q.add({ userId: 'user-1', kind: 'create', albumId: 'al-1', album, body });
  await q.flush();

  expect(q.getPending()[0]?.attempts).toBe(1);
  expect(q.getPending()[0]?.failure).toBeUndefined();
  expect(jest.getTimerCount()).toBe(1);
  expect(new ApiError(503, {}).status).toBe(503);
});
