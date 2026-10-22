import { act, fireEvent, render, screen } from '@testing-library/react-native';

import { ListScreen } from './ListScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn(), PATCH: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
const mockState = { status: 'signedIn', me: { handle: 'me' } };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: mockState }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false } }));

const item = (id: string, title: string, position: number) => ({
  album: { id, title, album_type: 'album', cover_url: null, artist: { id: 'ar-1', name: 'Radiohead' } },
  position,
  note: null,
});
const list = (handle: string) => ({
  id: 'l-1',
  user: { handle, deleted: false },
  title: 'Desert island',
  description: 'Ten records.',
  visibility: 'public',
  item_count: 2,
  created_at: '2026-10-21T12:00:00Z',
  items: [item('al-1', 'In Rainbows', 1), item('al-2', 'The Bends', 2)],
});

beforeEach(() => {
  jest.clearAllMocks();
  mockState.me.handle = 'me';
});

test('the owner can move and remove items', async () => {
  mockApi.GET.mockResolvedValue(ok(list('me')));
  mockApi.PATCH.mockResolvedValue(ok(list('me')));
  mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
  render(<ListScreen listId="l-1" />);

  expect(await screen.findByRole('header', { name: 'Desert island' })).toBeTruthy();
  expect(screen.getByText('Your list · 2 albums')).toBeTruthy();
  expect(screen.getByLabelText('Move In Rainbows up').props.accessibilityState.disabled).toBe(true);

  await act(async () => fireEvent.press(screen.getByLabelText('Move The Bends up')));
  expect(mockApi.PATCH).toHaveBeenCalledWith('/lists/{listId}/items/{albumId}', {
    params: { path: { listId: 'l-1', albumId: 'al-2' } },
    body: { position: 1 },
  });

  await act(async () => fireEvent.press(screen.getByLabelText('Remove In Rainbows')));
  expect(mockApi.DELETE).toHaveBeenCalledWith('/lists/{listId}/items/{albumId}', { params: { path: { listId: 'l-1', albumId: 'al-1' } } });
});

test('someone else just reads', async () => {
  mockState.me.handle = 'visitor';
  mockApi.GET.mockResolvedValue(ok(list('owner')));
  render(<ListScreen listId="l-1" />);

  expect(await screen.findByText('By @owner · 2 albums')).toBeTruthy();
  expect(screen.queryByLabelText('Remove In Rainbows')).toBeNull();
  expect(screen.queryByRole('button', { name: 'Delete list' })).toBeNull();
});
