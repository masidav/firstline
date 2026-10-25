import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { DailyPromptCard } from './DailyPromptCard';
import { PromptAnswersScreen } from './PromptAnswersScreen';
import { dayFrom, PromptsAdminScreen } from './PromptsAdminScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'ana', is_staff: true } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));

const prompt = { id: 'p1', day: '2026-10-25', text: "What's your best hypothetical band name?", source: 'library', open: true, answer_count: 0 };
const answer = (id: string, body: string, handle: string, extra: object = {}) => ({
  id, prompt_id: 'p1', user: { handle, deleted: false, display_name: null }, body, visibility: 'public',
  created_at: '2026-10-25T10:00:00Z', updated_at: '2026-10-25T10:00:00Z', mine: false, following: false, ...extra,
});

beforeEach(() => jest.clearAllMocks());

test('a fan answers the prompt followers-only, then sees answers from people they follow', async () => {
  mockApi.GET.mockResolvedValue(ok({ prompt, my_answer: null, following_answers: [] }));
  mockApi.PUT.mockResolvedValue(ok({
    prompt: { ...prompt, answer_count: 3 },
    my_answer: answer('a1', 'Face Value', 'ana', { mine: true, visibility: 'followers' }),
    following_answers: [answer('a2', 'The Velvet Ropes', 'ben', { following: true })],
  }));
  render(<DailyPromptCard />);

  expect(await screen.findByText("What's your best hypothetical band name?")).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Post answer' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText('Your answer'), '  Face Value ');
  fireEvent(screen.getByLabelText('Followers only'), 'valueChange', true);
  fireEvent.press(screen.getByRole('button', { name: 'Post answer' }));

  expect(await screen.findByText('“Face Value”')).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/prompts/{promptId}/answer', {
    params: { path: { promptId: 'p1' } },
    body: { body: 'Face Value', visibility: 'followers' },
  });
  expect(screen.getByText('Your answer · followers only')).toBeTruthy();
  expect(screen.getByText('“The Velvet Ropes”')).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'See all 3 answers' }));
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/prompts/p1');

  // Edit puts the answer back in the field.
  fireEvent.press(screen.getByRole('button', { name: 'Edit' }));
  expect(screen.getByLabelText('Your answer').props.value).toBe('Face Value');
});

test('deleting an answer reopens the prompt', async () => {
  mockApi.GET.mockResolvedValueOnce(ok({ prompt, my_answer: answer('a1', 'Face Value', 'ana', { mine: true }), following_answers: [] }))
    .mockResolvedValueOnce(ok({ prompt, my_answer: null, following_answers: [] }));
  mockApi.DELETE.mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
  render(<DailyPromptCard />);
  fireEvent.press(await screen.findByRole('button', { name: 'Delete your answer' }));
  expect(await screen.findByRole('button', { name: 'Post answer' })).toBeTruthy();
  expect(mockApi.DELETE).toHaveBeenCalledWith('/prompts/{promptId}/answer', { params: { path: { promptId: 'p1' } } });
});

test('all answers list yours first and others can be reported', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [answer('a1', 'Face Value', 'ana', { mine: true }), answer('a2', 'The Velvet Ropes', 'ben', { following: true, visibility: 'followers' })], next_cursor: null }));
  mockApi.POST.mockResolvedValue({ data: undefined, response: { ok: true, status: 202 } });
  render(<PromptAnswersScreen promptId="p1" />);

  expect(await screen.findByText('You')).toBeTruthy();
  expect(screen.getByText('@ben · you follow · followers only')).toBeTruthy();
  expect(screen.queryByRole('button', { name: "Report You's answer" })).toBeNull();
  fireEvent.press(screen.getByRole('button', { name: "Report @ben's answer" }));
  fireEvent.press(screen.getByRole('radio', { name: 'Spam' }));
  fireEvent.press(screen.getByRole('button', { name: 'Send report' }));
  await waitFor(() => expect(mockApi.POST).toHaveBeenCalledWith('/reports', { body: { target_type: 'prompt_answer', target_id: 'a2', reason: 'spam' } }));
});

test('staff schedule a prompt for tomorrow', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [] }));
  mockApi.PUT.mockResolvedValue(ok({ ...prompt, day: dayFrom(1), source: 'staff', open: false }));
  render(<PromptsAdminScreen />);
  expect(await screen.findByText('Nothing scheduled.')).toBeTruthy();
  fireEvent.changeText(screen.getByLabelText('Prompt'), 'Best tour merch ever?');
  fireEvent.press(screen.getByRole('button', { name: 'Schedule' }));
  expect(await screen.findByText(`Scheduled for ${dayFrom(1)}.`)).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/admin/prompts/{day}', { params: { path: { day: dayFrom(1) } }, body: { text: 'Best tour merch ever?' } });
  expect(dayFrom(1, new Date('2026-12-31T23:00:00Z'))).toBe('2027-01-01');
});
