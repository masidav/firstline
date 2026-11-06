import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import AppleCallback from '../../app/apple-callback';

// The /apple-callback route (src/app; tests can't live there, every file there is a route).

let mockStatus = 'signedOut';
let mockPending: { state: string; nonce: string } | null = null;
const mockSignInScreen = jest.fn();
const mockRedirect = jest.fn();

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: {}, state: { status: mockStatus } }) }));
jest.mock('expo-router', () => ({
  Redirect: (props: unknown) => {
    mockRedirect(props);
    return null;
  },
  router: { replace: jest.fn() },
}));
jest.mock('./appleSignIn', () => ({ takePendingAppleSignIn: () => mockPending }));
jest.mock('./SignInScreen', () => {
  const { Text } = jest.requireActual<typeof import('react-native')>('react-native');
  return {
    SignInScreen: (props: unknown) => {
      mockSignInScreen(props);
      return <Text>Finishing sign-in</Text>;
    },
  };
});

const replaceState = jest.fn();

/** The browser's address bar and history, as the route sees them. */
function visit(fragment: string) {
  Object.assign(globalThis, {
    window: {
      location: { href: `http://localhost:8081/apple-callback#${fragment}`, pathname: '/apple-callback', origin: 'http://localhost:8081' },
      history: { replaceState },
    },
  });
}

beforeEach(() => {
  jest.clearAllMocks();
  mockStatus = 'signedOut';
  mockPending = { state: 'S1', nonce: 'N1' };
});

test('a token with the state this tab started finishes the sign-in with the raw nonce, and leaves the address bar clean', async () => {
  visit('state=S1&id_token=apple.id.token');

  render(<AppleCallback />);

  expect(await screen.findByText('Finishing sign-in')).toBeTruthy();
  expect(mockSignInScreen).toHaveBeenCalledWith({ initialCredential: { provider: 'apple', idToken: 'apple.id.token', nonce: 'N1' } });
  expect(replaceState).toHaveBeenCalledWith(null, '', '/apple-callback');
});

test('a state that does not match the pending attempt is refused', async () => {
  visit('state=someone-elses&id_token=apple.id.token');
  const { router } = jest.requireMock('expo-router') as { router: { replace: jest.Mock } };

  render(<AppleCallback />);

  expect(await screen.findByText("This Apple sign-in expired or didn't start here. Try again.")).toBeTruthy();
  expect(mockSignInScreen).not.toHaveBeenCalled();
  fireEvent.press(screen.getByRole('button', { name: 'Back to sign-in' }));
  expect(router.replace).toHaveBeenCalledWith('/sign-in');
});

test('without a pending attempt (another tab, or a reload) the token is not used', async () => {
  visit('state=S1&id_token=apple.id.token');
  mockPending = null;

  render(<AppleCallback />);

  expect(await screen.findByText(/didn't start here/)).toBeTruthy();
});

test('backing out on Apple is said plainly; other errors are quoted', async () => {
  visit('state=S1&error=user_cancelled_authorize');
  render(<AppleCallback />);
  expect(await screen.findByText("You didn't continue with Apple.")).toBeTruthy();
  expect(screen.getByText('Nothing was saved.')).toBeTruthy();

  visit('state=S1&error=invalid_request');
  render(<AppleCallback />);
  expect(await screen.findByText('Apple said: invalid_request.')).toBeTruthy();
});

test('a fan who is already signed in goes home', async () => {
  visit('state=S1&id_token=t');
  mockStatus = 'signedIn';

  render(<AppleCallback />);

  await waitFor(() => expect(mockRedirect).toHaveBeenCalledWith(expect.objectContaining({ href: '/' })));
});
