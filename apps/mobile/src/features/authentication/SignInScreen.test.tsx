import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { ApiError } from '../../api/client';
import { SignInScreen } from './SignInScreen';

const mockSignIn = jest.fn();
const mockSession = { signIn: (...args: unknown[]) => mockSignIn(...args) };
let mockStatus = 'signedOut';

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession, state: { status: mockStatus } }),
}));
jest.mock('../../config', () => ({ config: { devSignIn: false, googleWebClientId: 'web-1', googleIosClientId: '' } }));
jest.mock('./googleSignIn', () => ({ googleClientId: 'web-1', startGoogleSignIn: jest.fn() }));
jest.mock('./appleSignIn', () => ({ isAppleSignInAvailable: jest.fn(), startAppleSignIn: jest.fn() }));
jest.mock('./AppleSignInButton', () => {
  const { Button } = jest.requireActual('../../ui/components');
  return { AppleSignInButton: (props: { onPress: () => void; disabled?: boolean }) => <Button label="Continue with Apple" {...props} /> };
});

const google = () => jest.requireMock('./googleSignIn').startGoogleSignIn as jest.Mock;
const apple = () => jest.requireMock('./appleSignIn') as { isAppleSignInAvailable: jest.Mock; startAppleSignIn: jest.Mock };
const handleRequired = new ApiError(422, { code: 'validation_failed', errors: [{ field: 'handle', message: 'Required to create an account.' }] });
const credential = { provider: 'google' as const, idToken: 'tok', nonce: 'n' };

beforeEach(() => {
  jest.clearAllMocks();
  mockStatus = 'signedOut';
  apple().isAppleSignInAvailable.mockResolvedValue(false);
});

test('on the web, Continue with Google leaves the page and the button stays busy', async () => {
  google().mockResolvedValue('redirecting');
  render(<SignInScreen />);

  fireEvent.press(screen.getByRole('button', { name: 'Continue with Google' }));

  await waitFor(() => expect(screen.getByRole('button', { name: 'Continue with Google' })).toBeDisabled());
  expect(mockSignIn).not.toHaveBeenCalled();
});

test('an existing account signs in with the credential the provider returned', async () => {
  google().mockResolvedValue({ ok: true, credential });
  mockSignIn.mockResolvedValue({ isNewUser: false });
  render(<SignInScreen />);

  fireEvent.press(screen.getByRole('button', { name: 'Continue with Google' }));

  await waitFor(() => expect(mockSignIn).toHaveBeenCalledWith(credential, undefined));
});

test('a new account is asked for a handle, prefilled from the token, then created with the same credential', async () => {
  const idToken = `h.${Buffer.from(JSON.stringify({ email: 'alice@example.test' })).toString('base64url')}.s`;
  google().mockResolvedValue({ ok: true, credential: { ...credential, idToken } });
  mockSignIn.mockRejectedValueOnce(handleRequired).mockResolvedValueOnce({ isNewUser: true });
  render(<SignInScreen />);

  fireEvent.press(screen.getByRole('button', { name: 'Continue with Google' }));

  const handle = await screen.findByLabelText('Handle');
  expect(handle.props.value).toBe('alice');
  fireEvent.changeText(handle, 'alice_fan');
  fireEvent.press(screen.getByRole('button', { name: 'Create account' }));

  await waitFor(() => expect(mockSignIn).toHaveBeenLastCalledWith({ ...credential, idToken }, 'alice_fan'));
});

test('a taken handle is shown on the handle field', async () => {
  google().mockResolvedValue({ ok: true, credential });
  mockSignIn.mockRejectedValueOnce(handleRequired).mockRejectedValueOnce(new ApiError(409, { code: 'handle_taken' }));
  render(<SignInScreen />);

  fireEvent.press(screen.getByRole('button', { name: 'Continue with Google' }));
  await screen.findByLabelText('Handle');
  fireEvent.press(screen.getByRole('button', { name: 'Create account' }));

  expect(await screen.findByText('That handle is taken. Try another.')).toBeTruthy();
});

test('an email already used by another account is explained, with a way back', async () => {
  google().mockResolvedValue({ ok: true, credential });
  mockSignIn.mockRejectedValueOnce(new ApiError(409, { code: 'email_in_use' }));
  render(<SignInScreen />);

  fireEvent.press(screen.getByRole('button', { name: 'Continue with Google' }));

  expect(await screen.findByText('Another account already uses this email. Sign in the way you did before.')).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Back to sign-in' }));
  expect(screen.getByRole('button', { name: 'Continue with Google' })).toBeEnabled();
});

test('backing out on the provider leaves the buttons as they were; a provider failure is shown', async () => {
  google().mockResolvedValueOnce('cancelled').mockResolvedValueOnce({ ok: false, message: "You didn't continue with Google." });
  render(<SignInScreen />);

  fireEvent.press(screen.getByRole('button', { name: 'Continue with Google' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Continue with Google' })).toBeEnabled());
  expect(screen.queryByRole('alert')).toBeNull();

  fireEvent.press(screen.getByRole('button', { name: 'Continue with Google' }));
  expect(await screen.findByText("You didn't continue with Google.")).toBeTruthy();
});

test('Continue with Apple shows only where Sign in with Apple is available', async () => {
  apple().isAppleSignInAvailable.mockResolvedValue(true);
  apple().startAppleSignIn.mockResolvedValue({ ok: true, credential: { provider: 'apple', idToken: 'a', nonce: 'n' } });
  mockSignIn.mockResolvedValue({ isNewUser: false });
  render(<SignInScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Continue with Apple' }));

  await waitFor(() => expect(mockSignIn).toHaveBeenCalledWith({ provider: 'apple', idToken: 'a', nonce: 'n' }, undefined));
});

test('a credential from the web callback is finished without pressing anything', async () => {
  mockSignIn.mockResolvedValue({ isNewUser: false });
  render(<SignInScreen initialCredential={credential} />);

  await waitFor(() => expect(mockSignIn).toHaveBeenCalledWith(credential, undefined));
  expect(google()).not.toHaveBeenCalled();
});

test('without any provider configured, the screen says so', () => {
  jest.requireMock('./googleSignIn').googleClientId = '';
  render(<SignInScreen />);

  expect(screen.getByText('No sign-in method is configured for this build yet.')).toBeTruthy();
  expect(screen.queryByRole('button')).toBeNull();
});
