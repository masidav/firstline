import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { SuspensionsScreen } from './SuspensionsScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn() } }));

const asking = {
  id: 'p1',
  user: { handle: 'botlike', deleted: false },
  reason: 'bot',
  message: null,
  note: 'Plays every second',
  started_at: '2026-10-24T12:00:00Z',
  ends_at: '2026-11-23T12:00:00Z',
  appeal: { id: 'a1', status: 'pending', message: 'Real fan', submitted_at: '2026-10-24T13:00:00Z' },
};
const quiet = { ...asking, id: 'p2', user: { handle: 'other', deleted: false }, reason: 'other', note: null, appeal: null };

beforeEach(() => jest.clearAllMocks());

test('staff deny a request with a reply, or lift a pause', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [asking, quiet], next_cursor: null }));
  mockApi.POST.mockImplementation(async (path: string) =>
    path === '/admin/appeals/{appealId}/decision'
      ? ok({ ...asking, appeal: { ...asking.appeal, status: 'denied', reply: 'Sorry, no.' } })
      : { data: undefined, response: { ok: true, status: 204 } },
  );
  render(<SuspensionsScreen />);

  expect(await screen.findByText('@botlike · Flagged as a bot · until Nov 23, 2026')).toBeTruthy();
  expect(screen.getByText('Staff note: Plays every second')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Deny request' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText('Reply to the fan (needed to deny)'), 'Sorry, no.');
  fireEvent.press(screen.getByRole('button', { name: 'Deny request' }));
  expect(await screen.findByText('Reply: Sorry, no.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/admin/appeals/{appealId}/decision', {
    params: { path: { appealId: 'a1' } },
    body: { decision: 'deny', reply: 'Sorry, no.' },
  });

  const lift = screen.getAllByRole('button', { name: 'Lift the pause' });
  fireEvent.press(lift[lift.length - 1]!);
  await waitFor(() => expect(screen.queryByText(/@other/)).toBeNull());
  expect(mockApi.POST).toHaveBeenLastCalledWith('/admin/users/{handle}/suspension/lift', { params: { path: { handle: 'other' } } });
});
