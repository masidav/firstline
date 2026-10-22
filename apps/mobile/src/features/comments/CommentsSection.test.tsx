import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { CommentsSection, threaded } from './CommentsSection';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn(), POST: jest.fn(), PATCH: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'me' } } }),
}));
jest.mock('expo-crypto', () => ({ randomUUID: () => 'key-1' }));

const comment = (id: string, handle: string, body: string | null, extra: Record<string, unknown> = {}) => ({
  id,
  log_id: 'log-1',
  user: { handle, deleted: false },
  parent_id: null,
  body,
  deleted: body === null,
  created_at: '2026-10-21T12:00:00Z',
  edited_at: null,
  ...extra,
});

beforeEach(() => jest.clearAllMocks());

test('shows the thread with replies indented and tombstones, and posts a comment that earns points', async () => {
  mockApi.GET.mockResolvedValue(
    ok({
      items: [comment('c1', 'other', null), comment('c2', 'other', 'A reply to a deleted one', { parent_id: 'c1' }), comment('c3', 'me', 'Mine')],
      next_cursor: null,
    }),
  );
  mockApi.POST.mockResolvedValue(ok({ comment: comment('c4', 'me', 'Long enough to count for points.'), points_awarded: [{ rule_key: 'comment_posted', points: 1 }] }, 201));
  render(<CommentsSection logId="log-1" />);

  expect(await screen.findByRole('header', { name: '2 comments' })).toBeTruthy();
  expect(screen.getByText('[deleted]')).toBeTruthy();
  expect(screen.getByText('A reply to a deleted one')).toBeTruthy();
  // Only my own comment has edit and delete.
  expect(screen.getAllByLabelText('Edit comment')).toHaveLength(1);

  fireEvent.changeText(screen.getByLabelText('Add a comment'), 'Long enough to count for points.');
  await act(async () => fireEvent.press(screen.getByRole('button', { name: 'Post' })));

  expect(await screen.findByText('You earned 1 point: comment.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/logs/{logId}/comments', {
    params: { path: { logId: 'log-1' } },
    headers: { 'Idempotency-Key': 'key-1' },
    body: { body: 'Long enough to count for points.' },
  });
});

test('replying sends the parent id and a short comment just says posted', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [comment('c1', 'other', 'First')], next_cursor: null }));
  mockApi.POST.mockResolvedValue(ok({ comment: comment('c2', 'me', 'Yes', { parent_id: 'c1' }), points_awarded: [] }, 201));
  render(<CommentsSection logId="log-1" />);

  fireEvent.press(await screen.findByLabelText('Reply to other'));
  expect(screen.getByText('Replying to @other')).toBeTruthy();
  fireEvent.changeText(screen.getByLabelText('Your reply'), 'Yes');
  await act(async () => fireEvent.press(screen.getByRole('button', { name: 'Post' })));

  expect(await screen.findByText('Posted.')).toBeTruthy();
  expect(mockApi.POST.mock.calls[0]?.[1].body).toEqual({ body: 'Yes', parent_id: 'c1' });
});

test('deleting my comment calls the API and reloads', async () => {
  mockApi.GET.mockResolvedValueOnce(ok({ items: [comment('c1', 'me', 'Mine')], next_cursor: null })).mockResolvedValue(ok({ items: [], next_cursor: null }));
  mockApi.DELETE.mockResolvedValue(ok(undefined, 204));
  render(<CommentsSection logId="log-1" />);

  const remove = await screen.findByLabelText('Delete comment');
  await act(async () => fireEvent.press(remove));

  await waitFor(() => expect(screen.queryByText('Mine')).toBeNull());
  expect(mockApi.DELETE).toHaveBeenCalledWith('/comments/{commentId}', { params: { path: { commentId: 'c1' } } });
});

test('threaded puts replies after their parent, one level deep', () => {
  const items = [
    comment('a', 'x', 'A'),
    comment('b', 'x', 'B'),
    comment('a1', 'x', 'A1', { parent_id: 'a' }),
    comment('a1a', 'x', 'A1a', { parent_id: 'a1' }),
    comment('orphan', 'x', 'O', { parent_id: 'missing' }),
  ];
  expect(threaded(items as never).map((t) => `${t.comment.id}:${t.depth}`)).toEqual(['a:0', 'a1:1', 'a1a:1', 'b:0', 'orphan:1']);
});
