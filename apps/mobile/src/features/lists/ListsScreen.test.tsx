import { act, fireEvent, render, screen } from '@testing-library/react-native';

import { ListsScreen } from './ListsScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'me' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false } }));

const list = (id: string, title: string, visibility = 'public', count = 0) => ({
  id,
  user: { handle: 'me', deleted: false },
  title,
  description: null,
  visibility,
  item_count: count,
  created_at: '2026-10-21T12:00:00Z',
});

beforeEach(() => jest.clearAllMocks());

test('lists my lists and creates a new one', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [list('l-1', 'Desert island', 'private', 3), list('l-2', 'To listen')], next_cursor: null }));
  mockApi.POST.mockResolvedValue(ok(list('l-3', 'Road trip'), 201));
  render(<ListsScreen />);

  expect(await screen.findByText('Desert island')).toBeTruthy();
  expect(screen.getByText('3 albums · private')).toBeTruthy();
  expect(screen.getByText('0 albums')).toBeTruthy();

  fireEvent.changeText(screen.getByLabelText('Title'), 'Road trip');
  fireEvent.press(screen.getByLabelText('Visibility Followers'));
  await act(async () => fireEvent.press(screen.getByRole('button', { name: 'Create list' })));

  expect(mockApi.POST).toHaveBeenCalledWith('/lists', { body: { title: 'Road trip', visibility: 'followers' } });
  const { router } = jest.requireMock('expo-router');
  expect(router.push).toHaveBeenCalledWith('/list/l-3');
});

test('with no lists it explains what lists are for', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [], next_cursor: null }));
  render(<ListsScreen />);

  expect(await screen.findByText(/No lists yet/)).toBeTruthy();
});
