import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { ReviewScreen } from './ReviewScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn() } }));

const item = (id: string) => ({
  id,
  artist_id: 'a1',
  artist: { id: 'a1', name: 'Geese', image_url: null },
  kind: 'merch',
  status: 'needs_review',
  submitted_at: '2026-10-21T12:00:00Z',
  can_share: false,
  files: [],
  caption: 'New hoodie',
  user: { handle: 'fan_one', deleted: false },
  checks: [],
});

beforeEach(() => jest.clearAllMocks());

test('staff approve, or reject with a reason the fan sees', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [item('s1'), item('s2')], next_cursor: null }));
  mockApi.POST.mockResolvedValue(ok({}));
  render(<ReviewScreen />);

  const approve = await screen.findAllByRole('button', { name: 'Approve' });
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
