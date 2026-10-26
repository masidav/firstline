import * as Crypto from 'expo-crypto';

// Randomness and hashing for sign-in (state, nonce, PKCE), through expo-crypto so the same code runs on the web
// and in the native app.

const UNRESERVED = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';

/** A random string of unreserved characters. */
export function randomString(length: number): string {
  // 66 characters: the modulo bias is negligible for a one-time secret of this length.
  return Array.from(Crypto.getRandomBytes(length), (byte) => UNRESERVED[byte % UNRESERVED.length]).join('');
}

/** SHA-256 as lowercase hex: what Sign in with Apple expects as the nonce (the API hashes the raw one to compare). */
export function sha256Hex(value: string): Promise<string> {
  return Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, value, { encoding: Crypto.CryptoEncoding.HEX });
}

/** base64url(SHA-256(verifier)), the S256 PKCE code challenge. */
export async function codeChallenge(verifier: string): Promise<string> {
  const digest = await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, verifier, {
    encoding: Crypto.CryptoEncoding.BASE64,
  });
  return digest.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
