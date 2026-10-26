/**
 * @jest-environment node
 */
import { codeChallenge } from './crypto';
import { callbackUri, startGoogleSignIn } from './googleSignIn.native';

const mockOpenAuthSession = jest.fn();
const mockFetch = jest.fn();

jest.mock('expo-web-browser', () => ({ openAuthSessionAsync: (...args: unknown[]) => mockOpenAuthSession(...args) }));
jest.mock('../../config', () => ({ config: { googleIosClientId: '123-abc.apps.googleusercontent.com' } }));
// Real hashing and randomness behind expo-crypto's API, so the PKCE math is checked for real.
jest.mock('expo-crypto', () => {
  const nodeCrypto = jest.requireActual<typeof import('node:crypto')>('node:crypto');
  return {
    CryptoDigestAlgorithm: { SHA256: 'SHA-256' },
    CryptoEncoding: { BASE64: 'base64', HEX: 'hex' },
    getRandomBytes: (n: number) => new Uint8Array(nodeCrypto.randomBytes(n)),
    digestStringAsync: async (_algorithm: string, data: string, options: { encoding: 'base64' | 'hex' }) =>
      nodeCrypto.createHash('sha256').update(data).digest(options.encoding),
  };
});

beforeEach(() => {
  jest.clearAllMocks();
  global.fetch = mockFetch;
});

test('the redirect is the reversed iOS client ID', () => {
  expect(callbackUri()).toBe('com.googleusercontent.apps.123-abc:/oauth2redirect');
});

test('signs in in place: account chooser in the auth session, code exchanged with PKCE, nonce kept for the API', async () => {
  mockOpenAuthSession.mockImplementation(async (url: string, redirect: string) => {
    const state = new URL(url).searchParams.get('state');
    return { type: 'success', url: `${redirect}?code=the%20code&state=${state}` };
  });
  mockFetch.mockResolvedValue(new Response(JSON.stringify({ id_token: 'id.tok.en' }), { status: 200 }));

  const outcome = await startGoogleSignIn();

  const [url, redirect] = mockOpenAuthSession.mock.calls[0] as [string, string];
  const authorize = new URL(url);
  expect(redirect).toBe('com.googleusercontent.apps.123-abc:/oauth2redirect');
  expect(authorize.searchParams.get('redirect_uri')).toBe(redirect);
  expect(authorize.searchParams.get('response_type')).toBe('code');
  expect(outcome).toEqual({
    ok: true,
    credential: { provider: 'google', idToken: 'id.tok.en', nonce: authorize.searchParams.get('nonce') },
  });

  const [tokenUrl, init] = mockFetch.mock.calls[0] as [string, RequestInit];
  expect(tokenUrl).toBe('https://oauth2.googleapis.com/token');
  const body = new URLSearchParams(init.body as string);
  expect(body.get('code')).toBe('the code');
  expect(body.get('client_id')).toBe('123-abc.apps.googleusercontent.com');
  expect(body.get('grant_type')).toBe('authorization_code');
  expect(body.get('redirect_uri')).toBe(redirect);
  await expect(codeChallenge(body.get('code_verifier') ?? '')).resolves.toBe(authorize.searchParams.get('code_challenge'));
});

test('closing the browser cancels without calling Google', async () => {
  mockOpenAuthSession.mockResolvedValue({ type: 'cancel' });

  await expect(startGoogleSignIn()).resolves.toBe('cancelled');
  expect(mockFetch).not.toHaveBeenCalled();
});

test('a redirect with another state is refused before any exchange', async () => {
  mockOpenAuthSession.mockImplementation(async (_url: string, redirect: string) => ({
    type: 'success',
    url: `${redirect}?code=c&state=someone-else`,
  }));

  await expect(startGoogleSignIn()).resolves.toMatchObject({ ok: false });
  expect(mockFetch).not.toHaveBeenCalled();
});

test('a failed token exchange is reported, not thrown', async () => {
  mockOpenAuthSession.mockImplementation(async (url: string, redirect: string) => ({
    type: 'success',
    url: `${redirect}?code=c&state=${new URL(url).searchParams.get('state')}`,
  }));
  mockFetch.mockResolvedValue(new Response('{"error":"invalid_grant"}', { status: 400 }));

  await expect(startGoogleSignIn()).resolves.toEqual({ ok: false, message: "Couldn't finish signing in with Google. Try again." });
});
