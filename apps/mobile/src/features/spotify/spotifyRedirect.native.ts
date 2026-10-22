import * as Crypto from 'expo-crypto';
import * as WebBrowser from 'expo-web-browser';

import { authorizeUrl, completeConnect, type ConnectOutcome, type SpotifyConnectBody, type SpotifyStatus } from './spotifyConnect';

// iOS/Android: Spotify's consent page opens in an in-app browser session (ASWebAuthenticationSession on iOS),
// which returns to the app on the fandomscore:// redirect. The attempt stays in memory, so nothing is stored.
// The redirect must be registered in the Spotify dashboard and listed in the API's Spotify__RedirectUris.

const UNRESERVED = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';

export function callbackUri(): string {
  return 'fandomscore://spotify-callback';
}

/** Runs the whole connect flow and sends the code to the API through `connect`. */
export async function startSpotifyConnect(
  clientId: string,
  connect: (body: SpotifyConnectBody) => Promise<SpotifyStatus>,
): Promise<ConnectOutcome> {
  const pending = { state: randomString(32), verifier: randomString(64), redirectUri: callbackUri() };
  const url = authorizeUrl({ clientId, redirectUri: pending.redirectUri, state: pending.state, challenge: await codeChallenge(pending.verifier) });

  const result = await WebBrowser.openAuthSessionAsync(url, pending.redirectUri);
  if (result.type !== 'success') {
    // The fan closed the browser (cancel or dismiss); nothing was granted.
    return 'cancelled';
  }
  return completeConnect(queryParams(result.url), pending, connect);
}

/** Native connects finish inside startSpotifyConnect; there is never a pending web redirect. */
export function takePendingConnect(): null {
  return null;
}

export function randomString(length: number): string {
  // 66 characters: the modulo bias is negligible for a one-time secret of this length.
  return Array.from(Crypto.getRandomBytes(length), (byte) => UNRESERVED[byte % UNRESERVED.length]).join('');
}

/** base64url(SHA-256(verifier)), the S256 code challenge. */
export async function codeChallenge(verifier: string): Promise<string> {
  const digest = await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, verifier, {
    encoding: Crypto.CryptoEncoding.BASE64,
  });
  return digest.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** The redirect's query (?code=&state= or ?error=), parsed by hand: React Native's URL support is partial. */
function queryParams(url: string): { code?: string; state?: string; error?: string } {
  const query = url.split('#')[0]?.split('?')[1] ?? '';
  const params: Record<string, string> = {};
  for (const pair of query.split('&')) {
    const [key, value = ''] = pair.split('=');
    if (key) params[decodeURIComponent(key)] = decodeURIComponent(value.replace(/\+/g, ' '));
  }
  return { code: params.code, state: params.state, error: params.error };
}
