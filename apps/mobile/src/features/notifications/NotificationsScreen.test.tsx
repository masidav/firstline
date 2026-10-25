import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { AlertsButton } from './AlertsButton';
import { ago, NotificationsScreen } from './NotificationsScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'ana' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));

const note = (id: string, read: boolean, kind = 'draw_selected') => ({
  id, kind, title: kind === 'draw_selected' ? 'You got tickets!' : 'Your ticket is here', body: 'Geese at Metro', link: '/tickets',
  created_at: new Date(Date.now() - 5 * 60_000).toISOString(), read, data: {},
});

beforeEach(() => jest.clearAllMocks());

test('lists notifications, opens one (marking it read), marks all read, and keeps the Alerts badge in step', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [note('n1', false), note('n2', false, 'ticket_delivered'), note('n3', true)], unread_count: 2, next_cursor: null }));
  mockApi.POST.mockResolvedValueOnce(ok({ unread_count: 1 })).mockResolvedValueOnce(ok({ unread_count: 0 }));
  render(
    <>
      <AlertsButton />
      <NotificationsScreen />
    </>,
  );

  expect(await screen.findByRole('button', { name: 'Alerts, 2 unread' })).toBeTruthy();
  expect(screen.getAllByText('5 min ago')).toHaveLength(3);
  fireEvent.press(screen.getByRole('button', { name: 'Unread: Your ticket is here. Geese at Metro' }));
  await waitFor(() => expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/tickets'));
  expect(mockApi.POST).toHaveBeenCalledWith('/me/notifications/read', { body: { ids: ['n2'] } });
  expect(await screen.findByRole('button', { name: 'Alerts, 1 unread' })).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Mark all read' }));
  expect(await screen.findByRole('button', { name: 'Alerts' })).toBeTruthy();
  expect(mockApi.POST).toHaveBeenLastCalledWith('/me/notifications/read', { body: {} });
  expect(screen.queryByRole('button', { name: 'Mark all read' })).toBeNull();
});

test('an empty list says what will show up; ago() reads naturally', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [], unread_count: 0, next_cursor: null }));
  render(<NotificationsScreen />);
  expect(await screen.findByText(/Nothing yet/)).toBeTruthy();
  const now = Date.parse('2026-10-25T12:00:00Z');
  expect(ago('2026-10-25T11:59:50Z', now)).toBe('just now');
  expect(ago('2026-10-25T09:00:00Z', now)).toBe('3 h ago');
  expect(ago('2026-10-23T12:00:00Z', now)).toBe('2 d ago');
});
