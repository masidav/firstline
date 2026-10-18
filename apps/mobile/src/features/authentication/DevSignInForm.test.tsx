import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { ApiError } from '../../api/client';
import { DevSignInForm } from './DevSignInForm';

const handleRequired = new ApiError(422, {
  code: 'validation_failed',
  errors: [{ field: 'handle', message: 'Required to create an account.' }],
});

test('a new test account is asked for a handle, then created', async () => {
  const signIn = jest.fn().mockRejectedValueOnce(handleRequired).mockResolvedValueOnce(undefined);
  render(<DevSignInForm signIn={signIn} />);

  fireEvent.changeText(screen.getByLabelText('Test account name'), 'alice');
  fireEvent.press(screen.getByRole('button', { name: 'Sign in' }));

  const handle = await screen.findByLabelText('Handle');
  expect(handle.props.value).toBe('alice');
  fireEvent.changeText(handle, 'alice_fan');
  fireEvent.press(screen.getByRole('button', { name: 'Create account' }));

  await waitFor(() => expect(signIn).toHaveBeenLastCalledWith('dev:alice', 'alice_fan'));
  expect(signIn).toHaveBeenNthCalledWith(1, 'dev:alice', undefined);
});

test('a taken handle is shown on the handle field', async () => {
  const signIn = jest
    .fn()
    .mockRejectedValueOnce(handleRequired)
    .mockRejectedValueOnce(new ApiError(409, { code: 'handle_taken' }));
  render(<DevSignInForm signIn={signIn} />);

  fireEvent.changeText(screen.getByLabelText('Test account name'), 'alice');
  fireEvent.press(screen.getByRole('button', { name: 'Sign in' }));
  await screen.findByLabelText('Handle');
  fireEvent.press(screen.getByRole('button', { name: 'Create account' }));

  expect(await screen.findByText('That handle is taken. Try another.')).toBeTruthy();
});

test('invalid names are caught before calling the API', () => {
  const signIn = jest.fn();
  render(<DevSignInForm signIn={signIn} />);

  fireEvent.changeText(screen.getByLabelText('Test account name'), 'no spaces');
  fireEvent.press(screen.getByRole('button', { name: 'Sign in' }));

  expect(screen.getByText('Use 3 to 30 letters, digits, or underscores.')).toBeTruthy();
  expect(signIn).not.toHaveBeenCalled();
});
