import { fireEvent, render, screen } from '@testing-library/react-native';

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
