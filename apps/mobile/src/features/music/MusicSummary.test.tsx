import { render, screen, waitFor } from '@testing-library/react-native';

import { notConnected } from './musicConnect';
import { MusicSummary, summarize } from './MusicSummary';

const mockApi = { GET: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));

beforeEach(() => jest.clearAllMocks());

test('summarize names both providers in a fixed order, connected or not', () => {
  expect(summarize([{ ...notConnected('tidal'), connected: true }])).toBe('Apple Music: not connected · Tidal: connected');
  expect(summarize([])).toBe('Apple Music: not connected · Tidal: not connected');
});

test('shows the summary from GET /me/music', async () => {
  mockApi.GET.mockResolvedValue({ data: { items: [{ ...notConnected('apple_music'), connected: true }] }, response: { ok: true, status: 200 } });

  render(<MusicSummary />);

  expect(await screen.findByText('Apple Music: connected · Tidal: not connected')).toBeTruthy();
});

test('shows nothing when the list cannot be loaded', async () => {
  mockApi.GET.mockResolvedValue({ error: { code: 'unauthorized' }, response: { ok: false, status: 401 } });

  const { toJSON } = render(<MusicSummary />);

  await waitFor(() => expect(mockApi.GET).toHaveBeenCalledWith('/me/music'));
  expect(toJSON()).toBeNull();
});
