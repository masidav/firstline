import { canUsePkce, codeChallenge, randomString } from './pkce';
import {
  authorizeUrl,
  type ConnectOutcome,
  type PendingConnect,
  type SpotifyConnectBody,
  type SpotifyStatus,
} from './spotifyConnect';

// Web: a full-page redirect to Spotify and back to /spotify-callback. (Popups are unreliable in a home-screen
// web app on iOS.) The native app uses expo-web-browser's auth session instead (spotifyRedirect.native.ts).

const PENDING_KEY = 'fandom-score.spotify-connect';

export function callbackUri(): string {
  return `${window.location.origin}/spotify-callback`;
}

/**
 * Leaves the app for Spotify's consent page; /spotify-callback finishes the connect. Throws if this page can't do
 * PKCE (plain http on a LAN address). The `connect` argument is for the native flow, which finishes in place.
 */
export async function startSpotifyConnect(
  clientId: string,
  _connect?: (body: SpotifyConnectBody) => Promise<SpotifyStatus>,
): Promise<ConnectOutcome> {
  if (!canUsePkce()) {
    throw new Error('Connecting Spotify needs HTTPS (or http://127.0.0.1). Open the app through a tunnel or 127.0.0.1.');
  }
  const pending: PendingConnect = { state: randomString(32), verifier: randomString(64), redirectUri: callbackUri() };
  const url = authorizeUrl({ clientId, redirectUri: pending.redirectUri, state: pending.state, challenge: await codeChallenge(pending.verifier) });
  sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));
  window.location.assign(url);
  return 'redirecting';
}

/** The attempt this tab started, removed as it is read so a code is only ever sent once. */
export function takePendingConnect(): PendingConnect | null {
  try {
    const value = sessionStorage.getItem(PENDING_KEY);
    sessionStorage.removeItem(PENDING_KEY);
    return value ? (JSON.parse(value) as PendingConnect) : null;
  } catch {
    return null;
  }
}
