import { fireEvent, render, screen, waitFor, within } from '@testing-library/react-native';

import { ReviewScreen } from './ReviewScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn() } }));

const item = (id: string, user: Record<string, unknown> = { handle: 'fan_one', deleted: false, avatar_url: 'https://api.test/v1/avatars/1' }) => ({
  id,
  artist_id: 'a1',
  artist: { id: 'a1', name: 'Geese', image_url: null },
  kind: 'merch',
  status: 'needs_review',
  submitted_at: '2026-10-21T12:00:00Z',
  can_share: false,
  files: [],
  caption: 'New hoodie',
  user,
  checks: [],
});

beforeEach(() => jest.clearAllMocks());

test('staff approve, or reject with a reason the fan sees', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [item('s1'), item('s2', { handle: 'fan_two', deleted: false, avatar_url: null })], next_cursor: null }));
  mockApi.POST.mockResolvedValue(ok({}));
  render(<ReviewScreen />);

  const approve = await screen.findAllByRole('button', { name: 'Approve' });
  // The fan's picture sits beside their handle; a fan without one gets their initial.
  expect(screen.getByLabelText("@fan_one's picture").props.source).toEqual({ uri: 'https://api.test/v1/avatars/1' });
  expect(within(screen.getByLabelText("@fan_two's picture")).getByText('F')).toBeTruthy();
  expect(screen.getByText('Merch · Geese · @fan_two')).toBeTruthy();
  fireEvent.press(approve[0]!);
  await waitFor(() => expect(screen.getAllByRole('button', { name: 'Approve' })).toHaveLength(1));
  expect(mockApi.POST).toHaveBeenCalledWith('/admin/submissions/{submissionId}/decision', { params: { path: { submissionId: 's1' } }, body: { decision: 'approve' } });

  expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText('Reason if rejecting (the fan sees it)'), 'Blurry');
  fireEvent.press(screen.getByRole('button', { name: 'Reject' }));
  expect(await screen.findByText('Nothing waiting.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenLastCalledWith('/admin/submissions/{submissionId}/decision', {
    params: { path: { submissionId: 's2' } },
    body: { decision: 'reject', reason: 'Blurry' },
  });
});

test('staff mark a merch photo not shareable with a note, see the note, and can allow sharing again', async () => {
  const merch = item('s1');
  mockApi.GET.mockResolvedValue(ok({ items: [merch, { ...item('s2'), kind: 'ticket' }], next_cursor: null }));
  mockApi.PUT.mockResolvedValueOnce(ok({ ...merch, share_blocked: true, share_blocked_note: 'Order number in view' }));
  render(<ReviewScreen />);

  // Only merch and concert photos can be shared, so only they get the action; the ticket has nothing to block.
  expect(await screen.findAllByRole('button', { name: 'Not shareable' })).toHaveLength(1);
  expect(screen.getAllByRole('button', { name: 'Approve' })).toHaveLength(2);
  fireEvent.changeText(screen.getByLabelText('Note if not shareable (staff only)'), 'Order number in view ');
  fireEvent.press(screen.getByRole('button', { name: 'Not shareable' }));

  expect(await screen.findByText('Not shareable: Order number in view')).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/admin/submissions/{submissionId}/shareable', {
    params: { path: { submissionId: 's1' } },
    body: { shareable: false, note: 'Order number in view' },
  });
  expect(screen.queryByRole('button', { name: 'Not shareable' })).toBeNull();
  // Approve and reject stay available: the mark is independent of the decision.
  expect(screen.getAllByRole('button', { name: 'Approve' })).toHaveLength(2);

  mockApi.PUT.mockResolvedValueOnce(ok({ ...merch, share_blocked: false, share_blocked_note: null }));
  fireEvent.press(screen.getByRole('button', { name: 'Allow sharing again' }));
  expect(await screen.findByRole('button', { name: 'Not shareable' })).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenLastCalledWith('/admin/submissions/{submissionId}/shareable', {
    params: { path: { submissionId: 's1' } },
    body: { shareable: true },
  });
  expect(screen.queryByText(/Not shareable:/)).toBeNull();
  expect(mockApi.POST).not.toHaveBeenCalled();
});

test('a not-shareable mark without a note sends no note', async () => {
  const merch = item('s1');
  mockApi.GET.mockResolvedValue(ok({ items: [merch], next_cursor: null }));
  mockApi.PUT.mockResolvedValueOnce(ok({ ...merch, share_blocked: true, share_blocked_note: null }));
  render(<ReviewScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Not shareable' }));
  expect(await screen.findByRole('button', { name: 'Allow sharing again' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Not shareable' })).toBeNull();
  expect(screen.getByText('Not shareable')).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/admin/submissions/{submissionId}/shareable', {
    params: { path: { submissionId: 's1' } },
    body: { shareable: false },
  });
});
