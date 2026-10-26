import { fireEvent, render, screen } from '@testing-library/react-native';

import { DailyTriviaCard, pointsLabel, resultsLine } from './DailyTriviaCard';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'ana' } } }) }));

const question = {
  id: 'q1', day: '2026-10-25', artist: { id: 'a1', name: 'Geese', image_url: null }, kind: 'release_year',
  prompt: 'What year did Geese release "3D Country"?', options: ['2021', '2022', '2023', '2024'], answered: false,
  chosen_index: null, answer_index: null, correct: null, points_for_right: 0.3, points_earned: null, results: null,
};

beforeEach(() => jest.clearAllMocks());

test('answering right shows the points; the options lock', async () => {
  mockApi.GET.mockResolvedValue(ok({ question }));
  mockApi.POST.mockResolvedValue(ok({ ...question, answered: true, chosen_index: 2, answer_index: 2, correct: true, points_earned: 0.3,
    results: { answered: 5, right: 3, percent_right: 60, following_right: [{ handle: 'ben', deleted: false, display_name: 'Ben' }, { handle: 'cat', deleted: false, display_name: null }] } }));
  render(<DailyTriviaCard />);

  expect(await screen.findByText('What year did Geese release "3D Country"?')).toBeTruthy();
  expect(screen.getByText('Answer right for +0.3 points toward your Geese score.')).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: '2023' }));
  expect(await screen.findByText('Right! +0.3 points for Geese. Back tomorrow for another.')).toBeTruthy();
  expect(screen.getByText('Ben and @cat got it right. 60% of fans did.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/me/trivia/{questionId}/answer', { params: { path: { questionId: 'q1' } }, body: { choice: 2 } });
  expect(screen.getByRole('button', { name: '2023, the right answer' })).toBeDisabled();
});

test('a wrong answer marks the pick and the right one', async () => {
  mockApi.GET.mockResolvedValue(ok({ question }));
  mockApi.POST.mockResolvedValue(ok({ ...question, answered: true, chosen_index: 0, answer_index: 2, correct: false, points_earned: 0 }));
  render(<DailyTriviaCard />);
  fireEvent.press(await screen.findByRole('button', { name: '2021' }));
  expect(await screen.findByText('Not this time. A new question tomorrow.')).toBeTruthy();
  expect(screen.getByRole('button', { name: '2021, your answer' })).toBeTruthy();
  expect(screen.getByRole('button', { name: '2023, the right answer' })).toBeTruthy();
});

test('no question, no card; points read naturally', async () => {
  mockApi.GET.mockResolvedValue(ok({ question: null }));
  const { toJSON } = render(<DailyTriviaCard />);
  await screen.findByText(/./).catch(() => undefined);
  expect(toJSON()).toBeNull();
  expect(pointsLabel(0.3)).toBe('+0.3 points');
  expect(pointsLabel(1)).toBe('+1 point');
});

test('results read naturally for one, none, and the first to answer', () => {
  const name = (u: { handle?: string | null; display_name?: string | null }) => u.display_name ?? `@${u.handle}`;
  const ben = { handle: 'ben', deleted: false, display_name: 'Ben' };
  expect(resultsLine({ answered: 4, right: 1, percent_right: 25, following_right: [ben] }, name)).toBe('Ben got it right. 25% of fans did.');
  expect(resultsLine({ answered: 4, right: 0, percent_right: 0, following_right: [] }, name)).toBe('Nobody you follow has it right yet. 0% of fans did.');
  expect(resultsLine({ answered: 1, right: 1, percent_right: 100, following_right: [] }, name)).toBe("You're the first fan to answer this one.");
});
