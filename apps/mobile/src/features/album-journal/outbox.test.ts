import { ApiError } from '../../api/client';
import { Outbox, type OutboxStorage } from './outbox';

function memoryStorage(): OutboxStorage {
  let value: string | null = null;
  return { read: () => value, write: (v) => (value = v) };
}

const entry = (key: string) => ({
  key,
  albumId: 'al-1',
  albumTitle: 'In Rainbows',
  body: { album_id: 'al-1', rating: 8 },
  createdAt: '2026-10-21T12:00:00Z',
});

const result = { log: { id: 'log-1' }, points_awarded: [] };

test('entries persist, replace by key, and can be removed', () => {
  const storage = memoryStorage();
  const outbox = new Outbox(storage);
  outbox.add(entry('k1'));
  outbox.add({ ...entry('k1'), albumTitle: 'Replaced' });
  outbox.add(entry('k2'));

  expect(new Outbox(storage).list().map((e) => [e.key, e.albumTitle])).toEqual([
    ['k1', 'Replaced'],
    ['k2', 'In Rainbows'],
  ]);
  outbox.remove('k1');
  expect(outbox.list().map((e) => e.key)).toEqual(['k2']);
});

test('flush sends with the saved Idempotency-Key, keeps network failures, and marks API rejections', async () => {
  const outbox = new Outbox(memoryStorage());
  outbox.add(entry('ok'));
  outbox.add(entry('offline'));
  outbox.add(entry('rejected'));
  const POST = jest.fn(async (_path: string, options: { headers: { 'Idempotency-Key': string } }) => {
    switch (options.headers['Idempotency-Key']) {
      case 'ok':
        return { data: result, response: { ok: true, status: 201 } };
      case 'offline':
        throw new TypeError('Failed to fetch');
      default:
        return { error: { code: 'validation_failed', errors: [{ field: 'rating', message: 'Bad rating.' }] }, response: { ok: false, status: 422 } };
    }
  });

  const sent = await outbox.flush({ POST } as never);

  expect(sent).toEqual([result]);
  expect(POST).toHaveBeenCalledWith('/logs', { headers: { 'Idempotency-Key': 'ok' }, body: { album_id: 'al-1', rating: 8 } });
  expect(outbox.list().map((e) => [e.key, e.failure])).toEqual([
    ['offline', undefined],
    ['rejected', 'Bad rating.'],
  ]);

  // A second flush skips the failed one and retries the offline one.
  POST.mockClear();
  await outbox.flush({ POST } as never);
  expect(POST.mock.calls.map(([, o]) => o.headers['Idempotency-Key'])).toEqual(['offline']);
});

test('garbage in storage is treated as empty', () => {
  const storage = memoryStorage();
  storage.write('not json');
  expect(new Outbox(storage).list()).toEqual([]);
  expect(new ApiError(500, {}).status).toBe(500);
});
