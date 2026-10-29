import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { AccountReviewScreen } from './AccountReviewScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn(), back: jest.fn(), canGoBack: () => false } }));

const flag = {
  id: 'f1', reason: 'streaming_farm', status: 'open', raised_at: '2026-10-27T12:00:00Z', decided_at: null,
  can_appeal: true, appeal: null,
};

beforeEach(() => jest.clearAllMocks());

test('a fan with no reviews sees a calm empty state', async () => {
  mockApi.GET.mockResolvedValue(ok({ on_hold: false, flags: [] }));
  render(<AccountReviewScreen />);

  expect(await screen.findByText("Your account isn't under review. Nothing to do here.")).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/me/fraud-review');
});

test('a fan on hold sees the reason in plain words and appeals once', async () => {
  mockApi.GET.mockResolvedValue(ok({ on_hold: true, flags: [flag] }));
  mockApi.POST.mockResolvedValue(
    ok({ id: 'ap1', flag_id: 'f1', message: 'I just love them', filed_at: '2026-10-28T12:00:00Z', status: 'pending', decided_at: null, response: null }, 201),
  );
  render(<AccountReviewScreen />);

  expect(await screen.findByText('About unusual listening patterns')).toBeTruthy();
  expect(screen.getByText(/on hold for fan tickets/)).toBeTruthy();
  expect(screen.getByText(/We're taking a look/)).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Send appeal' })).toBeDisabled();

  fireEvent.changeText(screen.getByLabelText('Your side'), '  I just love them  ');
  fireEvent.press(screen.getByRole('button', { name: 'Send appeal' }));
  await waitFor(() =>
    expect(mockApi.POST).toHaveBeenCalledWith('/me/fraud-review/flags/{flagId}/appeal', {
      params: { path: { flagId: 'f1' } },
      body: { message: 'I just love them' },
    }),
  );
  expect(await screen.findByText(/Your appeal is with our team/)).toBeTruthy();
  expect(screen.getByText('“I just love them”')).toBeTruthy();
  expect(screen.queryByLabelText('Your side')).toBeNull();
});

test('a decided appeal shows the outcome and the staff response, with no form', async () => {
  mockApi.GET.mockResolvedValue(
    ok({
      on_hold: false,
      flags: [{
        ...flag, reason: 'shared_device', status: 'cleared', decided_at: '2026-10-28T12:00:00Z', can_appeal: false,
        appeal: { id: 'ap1', flag_id: 'f1', message: 'My roommate', filed_at: '2026-10-27T13:00:00Z', status: 'granted',
                  decided_at: '2026-10-28T12:00:00Z', response: 'Thanks for explaining.' },
      }],
    }),
  );
  render(<AccountReviewScreen />);

  expect(await screen.findByText('About accounts sharing a device')).toBeTruthy();
  expect(screen.getByText(/All sorted/)).toBeTruthy();
  expect(screen.getByText('Your appeal was granted and the hold is lifted.')).toBeTruthy();
  expect(screen.getByText('Thanks for explaining.')).toBeTruthy();
  expect(screen.queryByLabelText('Your side')).toBeNull();
});

test('an appeal the server refuses shows its message', async () => {
  mockApi.GET.mockResolvedValue(ok({ on_hold: true, flags: [{ ...flag, status: 'upheld', reason: 'bot' }] }));
  mockApi.POST.mockResolvedValue({
    error: { type: 'about:blank', title: 'Conflict', status: 409, code: 'appeal_unavailable', detail: 'You already appealed this review.' },
    response: { ok: false, status: 409 },
  });
  render(<AccountReviewScreen />);

  expect(await screen.findByText('About automated activity')).toBeTruthy();
  fireEvent.changeText(screen.getByLabelText('Your side'), 'Not a bot');
  fireEvent.press(screen.getByRole('button', { name: 'Send appeal' }));
  expect(await screen.findByText('You already appealed this review.')).toBeTruthy();
});
