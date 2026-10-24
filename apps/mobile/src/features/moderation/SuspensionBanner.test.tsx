import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { SuspensionBanner } from './SuspensionBanner';

const mockApi = { POST: jest.fn() };
const mockUpdateMe = jest.fn();
const mockSession = { api: mockApi, updateMe: mockUpdateMe };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));

const suspension = {
  id: 'p1',
  reason: 'bot',
  message: 'Your account looks automated.',
  started_at: '2026-10-24T12:00:00Z',
  ends_at: '2026-11-23T12:00:00Z',
  appeal: null,
} as const;

beforeEach(() => jest.clearAllMocks());

test('a paused fan sees why and until when, and can ask once to be unbanned', async () => {
  const updated = { ...suspension, appeal: { id: 'a1', status: 'pending', message: 'Real fan', submitted_at: '2026-10-24T13:00:00Z' } };
  mockApi.POST.mockResolvedValue({ data: updated, response: { ok: true, status: 201 } });
  render(<SuspensionBanner suspension={suspension} />);

  expect(screen.getByText('Your account is paused until Nov 23, 2026')).toBeTruthy();
  expect(screen.getByText(/flagged activity on your account as automated/)).toBeTruthy();
  expect(screen.getByText('“Your account looks automated.”')).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Ask to be unbanned' }));
  expect(screen.getByRole('button', { name: 'Send request' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText('Tell staff why'), '  Real fan  ');
  fireEvent.press(screen.getByRole('button', { name: 'Send request' }));

  await waitFor(() => expect(mockUpdateMe).toHaveBeenCalledWith({ suspension: updated }));
  expect(mockApi.POST).toHaveBeenCalledWith('/me/suspension/appeal', { body: { message: 'Real fan' } });
});

test('a pending or denied request is shown instead of the button', () => {
  const pending = { ...suspension, appeal: { id: 'a1', status: 'pending', message: 'x', submitted_at: '2026-10-24T13:00:00Z' } } as const;
  const { rerender } = render(<SuspensionBanner suspension={pending} />);
  expect(screen.getByText('You asked to be unbanned on Oct 24, 2026. Staff will review it.')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Ask to be unbanned' })).toBeNull();

  rerender(<SuspensionBanner suspension={{ ...pending, appeal: { ...pending.appeal, status: 'denied', reply: 'One device, every second.' } }} />);
  expect(screen.getByText('Staff reviewed your request and kept the pause: “One device, every second.”')).toBeTruthy();
});
