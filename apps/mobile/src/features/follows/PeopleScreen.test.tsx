import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { PeopleScreen } from './PeopleScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const mockApi = { GET: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'ana' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));

const ben = { handle: 'ben', deleted: false, display_name: 'Ben', avatar_url: null, followed_by_me: true, follows_me: false };
const cat = { handle: 'cat', deleted: false, display_name: null, avatar_url: null, followed_by_me: false, follows_me: true };
const dee = { handle: 'dee', deleted: false, display_name: 'Dee', avatar_url: null, followed_by_me: false, follows_me: false };

function serve(following: unknown[], followers: unknown[], found: unknown[] = []) {
  mockApi.GET.mockImplementation(async (path: string) => {
    switch (path) {
      case '/me/following':
        return ok({ items: following, next_cursor: null });
      case '/me/followers':
        return ok({ items: followers, next_cursor: null });
      default:
        return ok({ items: found, next_cursor: null });
    }
  });
}

beforeEach(() => {
  jest.clearAllMocks();
  mockApi.PUT.mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
  mockApi.DELETE.mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
});

test('lists who you follow and your followers, with follow back and remove', async () => {
  serve([ben], [cat]);
  render(<PeopleScreen />);

  expect(await screen.findByRole('header', { name: 'You follow (1)' })).toBeTruthy();
  expect(screen.getByRole('header', { name: 'Your followers (1)' })).toBeTruthy();
  expect(screen.getByText('@cat · follows you')).toBeTruthy();

  // Follow back a follower.
  serve([ben, { ...cat, followed_by_me: true }], [{ ...cat, followed_by_me: true }]);
  fireEvent.press(screen.getByRole('button', { name: 'Follow @cat' }));
  expect(mockApi.PUT).toHaveBeenCalledWith('/users/{handle}/follow', { params: { path: { handle: 'cat' } } });
  expect(await screen.findByRole('header', { name: 'You follow (2)' })).toBeTruthy();

  // Unfollow Ben: he leaves the list.
  fireEvent.press(screen.getByRole('button', { name: 'Unfollow @ben' }));
  expect(mockApi.DELETE).toHaveBeenCalledWith('/users/{handle}/follow', { params: { path: { handle: 'ben' } } });
  await waitFor(() => expect(screen.queryByText('Ben')).toBeNull());

  // Remove Cat as a follower.
  fireEvent.press(screen.getByRole('button', { name: 'Remove follower @cat' }));
  expect(mockApi.DELETE).toHaveBeenCalledWith('/me/followers/{handle}', { params: { path: { handle: 'cat' } } });
  expect(await screen.findByText('No followers yet.')).toBeTruthy();
});

test('search finds fans to follow', async () => {
  serve([], [], [dee]);
  render(<PeopleScreen />);

  expect(await screen.findByText(/Nobody yet/)).toBeTruthy();
  const input = screen.getByLabelText('Find fans');
  fireEvent.changeText(input, 'de');
  fireEvent(input, 'submitEditing');

  expect(await screen.findByText('Dee')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/users', { params: { query: { q: 'de', limit: 25 } } });
  serve([{ ...dee, followed_by_me: true }], [], [dee]);
  fireEvent.press(screen.getByRole('button', { name: 'Follow @dee' }));
  expect(await screen.findByRole('header', { name: 'You follow (1)' })).toBeTruthy();
});
