/**
 * @jest-environment node
 */
const mockConfig = { apiUrl: 'https://api.example/v1', appleServicesId: 'com.example.web' };
jest.mock('../../config', () => ({
  get config() {
    return mockConfig;
  },
}));
jest.mock('expo-crypto', () => {
  const nodeCrypto = jest.requireActual<typeof import('node:crypto')>('node:crypto');
  return { getRandomBytes: (n: number) => new Uint8Array(nodeCrypto.randomBytes(n)) };
});

// Jest resolves `./appleSignIn` to the native file (iOS platform), so the web file is loaded by its full name.
const web = jest.requireActual('./appleSignIn.ts') as typeof import('./appleSignIn');

// The browser pieces the web flow touches, in place of a DOM.
const assign = jest.fn();
const store = new Map<string, string>();
const sessionStorage = {
  getItem: (key: string) => store.get(key) ?? null,
  setItem: (key: string, value: string) => void store.set(key, value),
  removeItem: (key: string) => void store.delete(key),
};
Object.assign(globalThis, { window: { location: { origin: 'http://localhost:8081', assign } }, sessionStorage });

beforeEach(() => {
  jest.clearAllMocks();
  store.clear();
  mockConfig.appleServicesId = 'com.example.web';
  mockConfig.apiUrl = 'https://api.example/v1';
});

test('leaves for Apple with the Services ID, the API callback as the return URL, a form post, and a state carrying this origin', async () => {
  await expect(web.startAppleSignIn()).resolves.toBe('redirecting');

  const url = new URL(assign.mock.calls[0][0] as string);
  expect(url.origin + url.pathname).toBe('https://appleid.apple.com/auth/authorize');
  const params = Object.fromEntries(url.searchParams);
  expect(params).toEqual({
    client_id: 'com.example.web',
    redirect_uri: 'https://api.example/v1/auth/apple/callback',
    response_type: 'code id_token',
    response_mode: 'form_post',
    scope: 'name email',
    state: expect.any(String),
    nonce: expect.stringMatching(/^[A-Za-z0-9\-._~]{32}$/),
  });
  // base64url JSON without padding: the API reads the origin, the random part makes it unguessable.
  expect(params.state).toMatch(/^[A-Za-z0-9_-]+$/);
  expect(JSON.parse(Buffer.from(params.state!, 'base64url').toString())).toEqual({
    o: 'http://localhost:8081',
    s: expect.stringMatching(/^[A-Za-z0-9\-._~]{32}$/),
  });
});

test('the state and the raw nonce are kept for the callback, and taken only once', async () => {
  await web.startAppleSignIn();
  const url = new URL(assign.mock.calls[0][0] as string);

  expect(web.takePendingAppleSignIn()).toEqual({ state: url.searchParams.get('state'), nonce: url.searchParams.get('nonce') });
  expect(web.takePendingAppleSignIn()).toBeNull();
});

test('the return URL is the API origin without /v1, whatever the trailing slash', async () => {
  mockConfig.apiUrl = 'http://localhost:5271/v1/';
  expect(web.appleCallbackUri()).toBe('http://localhost:5271/v1/auth/apple/callback');
});

test('available only with a Services ID', async () => {
  await expect(web.isAppleSignInAvailable()).resolves.toBe(true);
  mockConfig.appleServicesId = '';
  await expect(web.isAppleSignInAvailable()).resolves.toBe(false);
});

test('re-authentication before deletion cannot produce a code on the web; deletion goes ahead without one', async () => {
  await expect(web.reauthenticateWithApple()).resolves.toBe('failed');
});
