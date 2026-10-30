import { fireEvent, render, screen, waitFor, within } from '@testing-library/react-native';

import { FraudReviewScreen } from './FraudReviewScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn() } }));

const flag = (overrides: Record<string, unknown> = {}) => ({
  id: 'f1', user_id: 'u1', user: { handle: 'suspect', deleted: false }, reason: 'streaming_farm',
  evidence: { plays_per_hour: 400 }, note: 'Loops one track all night', source: 'staff', raised_by: 'staffer',
  raised_at: '2026-10-27T12:00:00Z', status: 'open', decided_by: null, decided_at: null, decision_note: null,
  appeal: null, appeal_decided_by: null, shown_to_fan_at: '2026-10-27T12:00:00Z',
  context: {
    account_created_at: '2026-10-01T00:00:00Z', trust_parts: { account_age: 0.25, spotify: 1 }, live_registrations: 2,
    held_reservations: 1, next_draw_closes_at: null,
  },
  ...overrides,
});

const appeal = { id: 'ap1', flag_id: 'f2', message: 'I really do love them', filed_at: '2026-10-28T12:00:00Z', status: 'pending', decided_at: null, response: null };

function queue(items: unknown[]) {
  mockApi.GET.mockImplementation((path: string) =>
    Promise.resolve(ok(path === '/admin/fraud-flags' ? { items, next_cursor: null } : { items: [], next_cursor: null })),
  );
}

beforeEach(() => jest.clearAllMocks());

test('staff see a flag with evidence and context, and uphold it with a note', async () => {
  queue([flag()]);
  mockApi.POST.mockResolvedValue(ok(flag({ status: 'upheld', decided_by: 'staffer', decided_at: '2026-10-28T12:00:00Z' })));
  render(<FraudReviewScreen />);

  expect(await screen.findByText('@suspect · unusual listening patterns · Open')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/admin/fraud-flags', { params: { query: { status: 'needs_action', limit: 50 } } });
  expect(screen.getByText('Note: Loops one track all night')).toBeTruthy();
  expect(screen.getByText(/"plays_per_hour": 400/)).toBeTruthy();
  expect(screen.getByText('Trust: account_age 0.25, spotify 1.00')).toBeTruthy();
  expect(screen.getByText('Live registrations: 2 · Tickets held: 1')).toBeTruthy();

  fireEvent.changeText(screen.getByLabelText('Note (staff only, optional)'), 'Confirmed farm');
  fireEvent.press(screen.getByRole('button', { name: 'Uphold' }));
  await waitFor(() =>
    expect(mockApi.POST).toHaveBeenCalledWith('/admin/fraud-flags/{flagId}/decision', {
      params: { path: { flagId: 'f1' } },
      body: { decision: 'uphold', note: 'Confirmed farm' },
    }),
  );
  // Upheld with no pending appeal no longer needs action.
  expect(await screen.findByText('Nothing needs a decision.')).toBeTruthy();
});

test('an unreviewed rule flag says the fan does not know and when the draw closes', async () => {
  queue([flag({
    source: 'rule', raised_by: null, note: null, shown_to_fan_at: null,
    context: { account_created_at: '2026-10-01T00:00:00Z', trust_parts: {}, live_registrations: 1, held_reservations: 0, next_draw_closes_at: '2026-11-02T18:00:00Z' },
  })]);
  render(<FraudReviewScreen />);

  expect(await screen.findByText(/by a rule/)).toBeTruthy();
  expect(screen.getByText("The fan doesn't know about this flag. Upholding it tells them.")).toBeTruthy();
  expect(screen.getByText(/^Review before .*: they're in a draw\.$/)).toBeTruthy();
});

test('staff answer a pending appeal with a response the fan sees', async () => {
  queue([flag({ id: 'f2', status: 'upheld', appeal })]);
  mockApi.POST.mockResolvedValue(
    ok(flag({ id: 'f2', status: 'cleared', appeal: { ...appeal, status: 'granted', response: 'Sorry about that' } })),
  );
  render(<FraudReviewScreen />);

  expect(await screen.findByText('“I really do love them”')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Uphold' })).toBeNull();
  fireEvent.changeText(screen.getByLabelText('Response to the fan (they see it, optional)'), 'Sorry about that');
  fireEvent.press(screen.getByRole('button', { name: 'Grant appeal (clear the flag)' }));
  await waitFor(() =>
    expect(mockApi.POST).toHaveBeenCalledWith('/admin/fraud-appeals/{appealId}/decision', {
      params: { path: { appealId: 'ap1' } },
      body: { decision: 'grant', response: 'Sorry about that' },
    }),
  );
  expect(await screen.findByText('Nothing needs a decision.')).toBeTruthy();
});

test('the status filter reloads the list; a cleared flag has no actions', async () => {
  queue([]);
  render(<FraudReviewScreen />);
  expect(await screen.findByText('Nothing needs a decision.')).toBeTruthy();

  queue([flag({ status: 'cleared', decided_by: 'staffer', decided_at: '2026-10-28T12:00:00Z', decision_note: 'Real fan' })]);
  fireEvent.press(within(screen.getByLabelText('Show')).getByRole('radio', { name: 'Cleared' }));
  expect(await screen.findByText('@suspect · unusual listening patterns · Cleared')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenLastCalledWith('/admin/fraud-flags', { params: { query: { status: 'cleared', limit: 50 } } });
  expect(screen.getByText(/by @staffer: Real fan/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Clear' })).toBeNull();
});

test('staff flag a fan by handle; a fan who already has an open flag is refused', async () => {
  queue([]);
  mockApi.POST.mockResolvedValueOnce(ok(flag({ id: 'f9', user: { handle: 'botty', deleted: false }, reason: 'bot', note: 'Scripted' }), 201));
  render(<FraudReviewScreen />);
  expect(await screen.findByText('Nothing needs a decision.')).toBeTruthy();

  expect(screen.getByRole('button', { name: 'Flag fan' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText("Fan's handle"), '@botty');
  fireEvent.press(within(screen.getByLabelText('Reason')).getByRole('radio', { name: 'automated activity' }));
  fireEvent.changeText(screen.getByLabelText('Flag note (staff only, optional)'), 'Scripted');
  fireEvent.press(screen.getByRole('button', { name: 'Flag fan' }));
  await waitFor(() =>
    expect(mockApi.POST).toHaveBeenCalledWith('/admin/fraud-flags', { body: { handle: 'botty', reason: 'bot', note: 'Scripted' } }),
  );
  expect(await screen.findByText(/Flagged @botty/)).toBeTruthy();
  expect(screen.getByText('@botty · automated activity · Open')).toBeTruthy();

  mockApi.POST.mockResolvedValueOnce({
    error: { type: 'about:blank', title: 'Conflict', status: 409, code: 'flag_open', detail: 'This fan already has an open flag.' },
    response: { ok: false, status: 409 },
  });
  fireEvent.changeText(screen.getByLabelText("Fan's handle"), 'botty');
  fireEvent.press(within(screen.getByLabelText('Reason')).getByRole('radio', { name: 'something else' }));
  fireEvent.press(screen.getByRole('button', { name: 'Flag fan' }));
  expect(await screen.findByText('This fan already has an open flag.')).toBeTruthy();
});

test('the audit lookup lists what happened to a fan and why', async () => {
  queue([]);
  render(<FraudReviewScreen />);
  expect(await screen.findByText('Nothing needs a decision.')).toBeTruthy();

  mockApi.GET.mockResolvedValueOnce(
    ok({
      items: [
        { id: 2, at: '2026-10-28T12:00:00Z', action: 'registration_blocked', reason: 'under_review', user_id: 'u1', handle: 'suspect',
          allocation_id: 'al-1', registration_id: null, flag_id: 'f1', appeal_id: null, actor: 'suspect', inputs: { flag_id: 'f1' } },
        { id: 1, at: '2026-10-27T12:00:00Z', action: 'flag_raised', reason: 'streaming_farm', user_id: 'u1', handle: 'suspect',
          allocation_id: null, registration_id: null, flag_id: 'f1', appeal_id: null, actor: null, inputs: {} },
      ],
      next_cursor: null,
    }),
  );
  fireEvent.changeText(screen.getByLabelText('Audit handle'), 'suspect');
  fireEvent.press(screen.getByRole('button', { name: 'Look up' }));

  expect(await screen.findByText('Registration blocked · under_review')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenLastCalledWith('/admin/eligibility-audit', { params: { query: { handle: 'suspect', limit: 50 } } });
  expect(screen.getByText(/@suspect · allocation al-1/)).toBeTruthy();
  expect(screen.getByText('Flag raised · streaming_farm')).toBeTruthy();
  expect(screen.getByText(/· system$/)).toBeTruthy();
  expect(screen.getByText(/"flag_id": "f1"/)).toBeTruthy();
});
