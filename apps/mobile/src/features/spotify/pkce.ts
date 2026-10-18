// PKCE (RFC 7636) for Spotify's authorization code flow. The server exchanges the code with the verifier,
// so the app never holds a client secret or a Spotify token (docs/adr/0005-spotify-integration.md).

const UNRESERVED = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';

/** A random string of unreserved characters (verifier: 43 to 128 characters). */
export function randomString(length = 64): string {
  const bytes = crypto.getRandomValues(new Uint8Array(length));
  // 66 characters: the modulo bias is negligible for a one-time secret of this length.
  return Array.from(bytes, (byte) => UNRESERVED[byte % UNRESERVED.length]).join('');
}

/** base64url(SHA-256(verifier)), the S256 code challenge. */
export async function codeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  let binary = '';
  new Uint8Array(digest).forEach((byte) => {
    binary += String.fromCharCode(byte);
  });
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** crypto.subtle only exists in secure contexts: HTTPS, or http://127.0.0.1 and http://localhost. */
export function canUsePkce(): boolean {
  return typeof crypto !== 'undefined' && typeof crypto.subtle?.digest === 'function';
}
