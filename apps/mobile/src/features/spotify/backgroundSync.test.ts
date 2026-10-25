import { resetBackgroundSync, syncOnOpen } from './backgroundSync';

const status = { connected: true, scopes: [], connected_at: null, last_synced_at: null, sync_state: 'queued', sync_error: null,
  tracked_artists_found: 0, top_artists_added: 0 };

beforeEach(() => resetBackgroundSync());

test('syncs once per launch, in the background, only when Spotify is connected', async () => {
  const api = { POST: jest.fn().mockResolvedValue({ data: status, response: { ok: true, status: 202 } }) };
  const update = jest.fn();

  await syncOnOpen(api as never, false, update);
  expect(api.POST).not.toHaveBeenCalled();

  await syncOnOpen(api as never, true, update);
  await syncOnOpen(api as never, true, update);
  expect(api.POST).toHaveBeenCalledTimes(1);
  expect(api.POST).toHaveBeenCalledWith('/me/spotify/sync', { params: { query: { background: true } } });
  expect(update).toHaveBeenCalledWith(status);
});

test('a failed background sync stays silent', async () => {
  const api = { POST: jest.fn().mockRejectedValue(new Error('offline')) };
  await expect(syncOnOpen(api as never, true, jest.fn())).resolves.toBeUndefined();
});
