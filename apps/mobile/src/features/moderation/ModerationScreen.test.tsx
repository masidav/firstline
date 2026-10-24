import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { ModerationScreen } from './ModerationScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn() } }));

const base = {
  author: { handle: 'author', deleted: false },
  artist: { id: 'a1', name: 'Geese', image_url: null },
  details: ['Insults another fan'],
  first_reported_at: '2026-10-24T12:00:00Z',
};
const log = {
  ...base,
  target_type: 'log',
  target_id: 'l1',
  photo: null,
  log: { album_id: 'al1', album_title: '3D Country', rating: 2, review: 'Rude review', visibility: 'public' },
  report_count: 3,
  reasons: [{ reason: 'abuse', count: 2 }, { reason: 'spam', count: 1 }],
};
const photo = {
  ...base,
  target_type: 'photo',
  target_id: 's1',
  photo: { kind: 'merch', caption: 'Hoodie', files: [] },
  log: null,
  report_count: 1,
  reasons: [{ reason: 'inappropriate', count: 1 }],
  details: [],
};

beforeEach(() => jest.clearAllMocks());

test('staff take a post down with a note, or dismiss its reports', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [log, photo], next_cursor: null }));
  mockApi.POST.mockResolvedValue(ok({}));
  render(<ModerationScreen />);

  expect(await screen.findByText('3 reports: Abuse or harassment ×2, Spam')).toBeTruthy();
  expect(screen.getByText('“Insults another fan”')).toBeTruthy();
  fireEvent.changeText(screen.getAllByLabelText('Note (staff only, optional)')[0]!, 'Abusive');
  fireEvent.press(screen.getAllByRole('button', { name: 'Take down' })[0]!);
  await waitFor(() => expect(screen.queryByText('Rude review')).toBeNull());
  expect(mockApi.POST).toHaveBeenCalledWith('/admin/reports/{targetType}/{targetId}/decision', {
    params: { path: { targetType: 'log', targetId: 'l1' } },
    body: { action: 'take_down', note: 'Abusive' },
  });

  fireEvent.press(screen.getByRole('button', { name: 'Dismiss reports' }));
  expect(await screen.findByText('No open reports.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenLastCalledWith('/admin/reports/{targetType}/{targetId}/decision', {
    params: { path: { targetType: 'photo', targetId: 's1' } },
    body: { action: 'dismiss' },
  });
});

test("staff can flag a reported post's author as a bot", async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [log], next_cursor: null }));
  mockApi.POST.mockResolvedValue({ data: {}, response: { ok: true, status: 201 } });
  render(<ModerationScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Flag author as a bot (pause 30 days)' }));

  expect(await screen.findByText('@author is paused for 30 days.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/admin/users/{handle}/suspension', {
    params: { path: { handle: 'author' } },
    body: { reason: 'bot', days: 30 },
  });
});
