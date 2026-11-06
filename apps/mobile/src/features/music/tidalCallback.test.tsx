import { render, screen, waitFor } from '@testing-library/react-native';

import TidalCallback from '../../app/tidal-callback';
import { notConnected } from './musicConnect';

// The /tidal-callback route (src/app; tests can't live there, every file there is a route).

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

let mockStatus = 'signedIn';
let mockParams: { code?: string; state?: string; error?: string } = {};
let mockPending: { state: string; verifier: string; redirectUri: string } | null = null;
const mockApi = { PUT: jest.fn() };
const mockSession = { api: mockApi };
const mockRedirect = jest.fn();

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: mockStatus } }) }));
jest.mock('expo-router', () => ({
  Redirect: (props: unknown) => {
    mockRedirect(props);
    return null;
  },
  router: { replace: jest.fn() },
  useLocalSearchParams: () => mockParams,
}));
jest.mock('./tidalRedirect', () => ({ takePendingTidalConnect: () => mockPending }));

const { router } = jest.requireMock<{ router: { replace: jest.Mock } }>('expo-router');
const pending = { state: 'S1', verifier: 'v'.repeat(64), redirectUri: 'http://127.0.0.1:8081/tidal-callback' };

beforeEach(() => {
  jest.clearAllMocks();
  mockStatus = 'signedIn';
  mockPending = pending;
  mockParams = { code: 'the-code', state: 'S1' };
});

test('a code with the state this tab started goes to PUT /me/music/tidal, then on to the music screen', async () => {
  mockApi.PUT.mockResolvedValue(ok({ ...notConnected('tidal'), connected: true, sync_state: 'queued' }));

  render(<TidalCallback />);

  await waitFor(() => expect(router.replace).toHaveBeenCalledWith('/music'));
  expect(mockApi.PUT).toHaveBeenCalledWith('/me/music/{provider}', {
    params: { path: { provider: 'tidal' } },
    body: { code: 'the-code', code_verifier: pending.verifier, redirect_uri: pending.redirectUri },
  });
});

test('a state that does not match the pending attempt is refused and nothing is sent', async () => {
  mockParams = { code: 'the-code', state: 'someone-elses' };

  render(<TidalCallback />);

  expect(await screen.findByText('Tidal not connected')).toBeTruthy();
  expect(screen.getByText(/didn't start here/)).toBeTruthy();
  expect(mockApi.PUT).not.toHaveBeenCalled();
  expect(router.replace).not.toHaveBeenCalled();
});

test('an API refusal is shown with the reason', async () => {
  mockApi.PUT.mockResolvedValue({ error: { code: 'music_in_use' }, response: { ok: false, status: 409 } });

  render(<TidalCallback />);

  expect(await screen.findByText('This Tidal account is already connected to another Fandom Score account.')).toBeTruthy();
});

test('a signed-out fan is sent to the start without sending the code', () => {
  mockStatus = 'signedOut';

  render(<TidalCallback />);

  expect(mockRedirect).toHaveBeenCalledWith(expect.objectContaining({ href: '/' }));
  expect(mockApi.PUT).not.toHaveBeenCalled();
});
