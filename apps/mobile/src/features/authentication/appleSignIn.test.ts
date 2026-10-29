/**
 * @jest-environment node
 */
import { createHash } from 'node:crypto';

import { isAppleSignInAvailable, reauthenticateWithApple, startAppleSignIn } from './appleSignIn';

const mockSignIn = jest.fn();
const mockAvailable = jest.fn();

jest.mock('expo-apple-authentication', () => ({
  AppleAuthenticationScope: { FULL_NAME: 0, EMAIL: 1 },
  isAvailableAsync: () => mockAvailable(),
  signInAsync: (options?: unknown) => mockSignIn(options),
}));
const mockExtra: { appleSignIn?: boolean } = {};
// A getter: the factory runs during import hoisting, before mockExtra is assigned.
jest.mock('expo-constants', () => ({ __esModule: true, default: { expoConfig: { get extra() { return mockExtra; } } } }));
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

beforeEach(() => jest.clearAllMocks());

test('Apple gets the hashed nonce; the API gets the raw one with the identity token', async () => {
  mockSignIn.mockResolvedValue({ identityToken: 'apple.id.token', authorizationCode: 'code', user: 'u' });

  const outcome = await startAppleSignIn();

  expect(outcome).toMatchObject({ ok: true, credential: { provider: 'apple', idToken: 'apple.id.token' } });
  const nonce = (outcome as { credential: { nonce: string } }).credential.nonce;
  const options = mockSignIn.mock.calls[0][0] as { nonce: string; requestedScopes: number[] };
  expect(options.requestedScopes).toEqual([0, 1]);
  expect(options.nonce).toBe(createHash('sha256').update(nonce).digest('hex'));
});

test('backing out of the Apple sheet cancels; other failures are a message', async () => {
  mockSignIn.mockRejectedValueOnce({ code: 'ERR_REQUEST_CANCELED' });
  await expect(startAppleSignIn()).resolves.toBe('cancelled');

  mockSignIn.mockRejectedValueOnce(new Error('ERR_REQUEST_FAILED'));
  await expect(startAppleSignIn()).resolves.toEqual({ ok: false, message: "Couldn't sign in with Apple. Try again." });
});

test('re-authentication before deletion returns the authorization code, or why there is none', async () => {
  mockSignIn.mockResolvedValueOnce({ identityToken: 't', authorizationCode: 'fresh-code', user: 'u' });
  await expect(reauthenticateWithApple()).resolves.toEqual({ code: 'fresh-code' });

  mockSignIn.mockRejectedValueOnce({ code: 'ERR_REQUEST_CANCELED' });
  await expect(reauthenticateWithApple()).resolves.toBe('cancelled');

  mockSignIn.mockRejectedValueOnce(new Error('no network'));
  await expect(reauthenticateWithApple()).resolves.toBe('failed');
});

test('availability follows the module, and a throwing module means unavailable', async () => {
  mockAvailable.mockResolvedValueOnce(true);
  await expect(isAppleSignInAvailable()).resolves.toBe(true);
  mockAvailable.mockRejectedValueOnce(new Error('no module'));
  await expect(isAppleSignInAvailable()).resolves.toBe(false);
});

describe('a build without the Sign in with Apple entitlement', () => {
  afterEach(() => {
    delete mockExtra.appleSignIn;
  });

  it('hides the button even where the module is available', async () => {
    mockAvailable.mockResolvedValue(true);
    mockExtra.appleSignIn = false;
    await expect(isAppleSignInAvailable()).resolves.toBe(false);
  });

  it('asks the module when the build has the entitlement', async () => {
    mockAvailable.mockResolvedValue(true);
    mockExtra.appleSignIn = true;
    await expect(isAppleSignInAvailable()).resolves.toBe(true);
  });
});
