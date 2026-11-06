/**
 * @jest-environment node
 */
import { codeChallenge } from '../spotify/spotifyRedirect.native';
import { notConnected } from './musicConnect';
import { startTidalConnect } from './tidalRedirect.native';

const mockOpenAuthSession = jest.fn();

jest.mock('expo-web-browser', () => ({ openAuthSessionAsync: (...args: unknown[]) => mockOpenAuthSession(...args) }));
// Real hashing and randomness behind expo-crypto's API, so the PKCE math is checked for real.
jest.mock('expo-crypto', () => {
  const nodeCrypto = jest.requireActual<typeof import('node:crypto')>('node:crypto');
  return {
    CryptoDigestAlgorithm: { SHA256: 'SHA-256' },
    CryptoEncoding: { BASE64: 'base64' },
    getRandomBytes: (n: number) => new Uint8Array(nodeCrypto.randomBytes(n)),
    digestStringAsync: async (_algorithm: string, data: string) =>
      nodeCrypto.createHash('sha256').update(data).digest('base64'),
  };
});

const connected = { ...notConnected('tidal'), connected: true, sync_state: 'queued' as const };

beforeEach(() => jest.clearAllMocks());

test('connects in place: consent in the auth session, then the code goes to the API', async () => {
  mockOpenAuthSession.mockImplementation(async (url: string, redirect: string) => {
    const state = new URL(url).searchParams.get('state');
    return { type: 'success', url: `${redirect}?code=the%20code&state=${state}` };
  });
  const connect = jest.fn().mockResolvedValue(connected);

  const outcome = await startTidalConnect('client-1', connect);

  expect(outcome).toEqual({ ok: true, status: connected });
  const [url, redirect] = mockOpenAuthSession.mock.calls[0] as [string, string];
  expect(redirect).toBe('fandomscore://tidal-callback');
  const authorize = new URL(url);
  expect(authorize.origin + authorize.pathname).toBe('https://login.tidal.com/authorize');
  expect(authorize.searchParams.get('redirect_uri')).toBe('fandomscore://tidal-callback');
  expect(authorize.searchParams.get('code_challenge_method')).toBe('S256');
  const body = connect.mock.calls[0][0];
  expect(body.code).toBe('the code');
  expect(body.redirect_uri).toBe('fandomscore://tidal-callback');
  await expect(codeChallenge(body.code_verifier)).resolves.toBe(authorize.searchParams.get('code_challenge'));
});

test('closing the browser cancels without calling the API', async () => {
  mockOpenAuthSession.mockResolvedValue({ type: 'cancel' });
  const connect = jest.fn();

  await expect(startTidalConnect('client-1', connect)).resolves.toBe('cancelled');
  expect(connect).not.toHaveBeenCalled();
});

test('a redirect with another state is refused', async () => {
  mockOpenAuthSession.mockImplementation(async (_url: string, redirect: string) => ({
    type: 'success',
    url: `${redirect}?code=c&state=someone-else`,
  }));
  const connect = jest.fn();

  const outcome = await startTidalConnect('client-1', connect);

  expect(outcome).toMatchObject({ ok: false });
  expect(connect).not.toHaveBeenCalled();
});
