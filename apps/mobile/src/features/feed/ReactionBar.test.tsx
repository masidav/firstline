import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { REACTIONS, ReactionBar } from './ReactionBar';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));

beforeEach(() => jest.clearAllMocks());

test('shows counts, adds a reaction from the picker, and takes one back', async () => {
  mockApi.PUT.mockResolvedValue(ok({ reactions: [{ reaction: 'fire', count: 3, mine: false }, { reaction: 'heart', count: 1, mine: true }] }));
  mockApi.DELETE.mockResolvedValue(ok({ reactions: [{ reaction: 'fire', count: 3, mine: false }] }));
  render(<ReactionBar targetType="log" targetId="l1" initial={[{ reaction: 'fire', count: 3, mine: false }]} />);

  expect(screen.getByRole('button', { name: 'fire, 3: tap to add yours' })).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Add a reaction' }));
  fireEvent.press(screen.getByRole('button', { name: 'React with heart' }));
  expect(await screen.findByRole('button', { name: 'heart, 1, yours: tap to take it back' })).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/reactions/{targetType}/{targetId}/{reaction}', { params: { path: { targetType: 'log', targetId: 'l1', reaction: 'heart' } } });

  fireEvent.press(screen.getByRole('button', { name: 'heart, 1, yours: tap to take it back' }));
  expect(mockApi.DELETE).toHaveBeenCalledWith('/reactions/{targetType}/{targetId}/{reaction}', { params: { path: { targetType: 'log', targetId: 'l1', reaction: 'heart' } } });
  expect(await screen.findByRole('button', { name: 'Add a reaction' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: /^heart/ })).toBeNull();
});

test('six emoji, in a fixed order', () => {
  expect(REACTIONS.map((r) => r.emoji).join('')).toBe('🔥❤️😂🤘😮👏');
});

// Reactions replaced comments (decided 2026-10-25), so the states the comments checklist asked for live here.

test('a failed reaction changes nothing, and a second try goes through', async () => {
  mockApi.PUT.mockResolvedValueOnce({ error: { code: 'internal_error' }, response: { ok: false, status: 503 } })
    .mockResolvedValueOnce(ok({ reactions: [{ reaction: 'fire', count: 4, mine: true }] }));
  render(<ReactionBar targetType="photo" targetId="s1" initial={[{ reaction: 'fire', count: 3, mine: false }]} />);

  fireEvent.press(screen.getByRole('button', { name: 'fire, 3: tap to add yours' }));
  await waitFor(() => expect(mockApi.PUT).toHaveBeenCalledTimes(1));
  // Still 3 and not yours; the "+" is back too.
  expect(await screen.findByRole('button', { name: 'fire, 3: tap to add yours' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Add a reaction' })).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'fire, 3: tap to add yours' }));
  expect(await screen.findByRole('button', { name: 'fire, 4, yours: tap to take it back' })).toBeTruthy();
});

test('taps while a reaction is in flight are ignored', async () => {
  let finish!: (value: unknown) => void;
  mockApi.PUT.mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  render(<ReactionBar targetType="log" targetId="l1" initial={[{ reaction: 'fire', count: 3, mine: false }, { reaction: 'clap', count: 1, mine: false }]} />);

  fireEvent.press(screen.getByRole('button', { name: 'fire, 3: tap to add yours' }));
  fireEvent.press(screen.getByRole('button', { name: 'fire, 3: tap to add yours' }));
  fireEvent.press(screen.getByRole('button', { name: 'clapping, 1: tap to add yours' }));
  expect(mockApi.PUT).toHaveBeenCalledTimes(1);

  finish(ok({ reactions: [{ reaction: 'fire', count: 4, mine: true }, { reaction: 'clap', count: 1, mine: false }] }));
  expect(await screen.findByRole('button', { name: 'fire, 4, yours: tap to take it back' })).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'clapping, 1: tap to add yours' }));
  expect(mockApi.PUT).toHaveBeenCalledTimes(2);
});

test('the picker leaves out reactions you already gave, and unknown keys from the server are skipped', () => {
  render(
    <ReactionBar
      targetType="prompt_answer"
      targetId="pa1"
      initial={[{ reaction: 'heart', count: 2, mine: true }, { reaction: 'shelby_1' as never, count: 1, mine: false }]}
    />,
  );

  // A reaction key this build does not know (a future custom emoji) is not drawn, and nothing crashes.
  expect(screen.getAllByRole('button')).toHaveLength(2);
  fireEvent.press(screen.getByRole('button', { name: 'Add a reaction' }));
  expect(screen.queryByRole('button', { name: 'React with heart' })).toBeNull();
  expect(screen.getAllByRole('button', { name: /^React with/ })).toHaveLength(5);
  expect(screen.queryByRole('button', { name: 'Add a reaction' })).toBeNull();
});
