import { fireEvent, render, screen } from '@testing-library/react-native';

import { MyPostsScreen } from './MyPostsScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const mockApi = { GET: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn() } }));

const base = {
  artist_id: 'a1',
  artist: { id: 'a1', name: 'Geese', image_url: null },
  submitted_at: '2026-10-21T12:00:00Z',
  files: [],
  points_awarded: [],
  caption: null,
  shared_at: null,
  rejection_reason: null,
};
const verified = { ...base, id: 's1', kind: 'concert_photo', status: 'verified', can_share: true, points_awarded: [{ rule_key: 'concert_photo_verified', points: 5 }] };
const pending = { ...base, id: 's2', kind: 'ticket', status: 'needs_review', can_share: false };

beforeEach(() => jest.clearAllMocks());

test('a verified photo can be shared; a pending ticket can be withdrawn', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [verified, pending], next_cursor: null }));
  mockApi.PUT.mockResolvedValue(ok({ ...verified, shared_at: '2026-10-21T13:00:00Z' }));
  mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
  render(<MyPostsScreen />);

  expect(await screen.findByText('+5 points')).toBeTruthy();
  expect(screen.getByText('Waiting for review')).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Share to feed' }));
  expect(await screen.findByText('Verified · on the feed')).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/submissions/{submissionId}/share', { params: { path: { submissionId: 's1' } }, body: { shared: true } });
  expect(screen.getByRole('button', { name: 'Remove from feed' })).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Withdraw' }));
  expect(await screen.findByRole('button', { name: 'Remove from feed' })).toBeTruthy();
  expect(screen.queryByText('Waiting for review')).toBeNull();
});
