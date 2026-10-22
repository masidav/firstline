import { act, fireEvent, render, screen } from '@testing-library/react-native';

import { AlbumScreen } from './AlbumScreen';
import { outbox } from './outbox';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), PATCH: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'me' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false } }));
jest.mock('expo-crypto', () => ({ randomUUID: () => 'offline-key' }));

const album = {
  id: 'al-1',
  artist: { id: 'ar-1', name: 'Radiohead', image_url: null },
  title: 'In Rainbows',
  album_type: 'album',
  release_date: '2007-10-10',
  release_date_precision: 'day',
  cover_url: null,
  spotify_album_id: 'sp-al-1',
  delisted: false,
  community: { log_count: 0, average_rating: null },
  tracks: [],
  my_latest_log: null,
};

beforeEach(() => {
  jest.clearAllMocks();
  for (const entry of outbox().list()) outbox().remove(entry.key);
});

test('a log that cannot reach the server is kept in the outbox with its Idempotency-Key', async () => {
  mockApi.GET.mockImplementation(async (path: string) => (path === '/albums/{albumId}' ? ok(album) : ok({ items: [], next_cursor: null })));
  mockApi.POST.mockRejectedValue(new TypeError('Failed to fetch'));
  render(<AlbumScreen albumId="al-1" />);

  fireEvent.press(await screen.findByRole('button', { name: 'Log this album' }));
  fireEvent.press(screen.getByLabelText('Rate 4 stars'));
  await act(async () => fireEvent.press(screen.getByRole('button', { name: 'Save log' })));

  expect(await screen.findByText(/Saved on this device/)).toBeTruthy();
  const [entry] = outbox().list();
  expect(entry).toMatchObject({ key: 'offline-key', albumId: 'al-1', albumTitle: 'In Rainbows', body: { album_id: 'al-1', rating: 8 } });
  expect(entry?.failure).toBeUndefined();
});

test('an API rejection is shown in the form, not queued', async () => {
  mockApi.GET.mockImplementation(async (path: string) => (path === '/albums/{albumId}' ? ok(album) : ok({ items: [], next_cursor: null })));
  mockApi.POST.mockResolvedValue({
    error: { code: 'validation_failed', errors: [{ field: 'listened_on', message: 'Bad date.' }] },
    response: { ok: false, status: 422 },
  });
  render(<AlbumScreen albumId="al-1" />);

  fireEvent.press(await screen.findByRole('button', { name: 'Log this album' }));
  await act(async () => fireEvent.press(screen.getByRole('button', { name: 'Save log' })));

  expect(await screen.findByText('Bad date.')).toBeTruthy();
  expect(outbox().list()).toEqual([]);
});
