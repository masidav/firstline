import { canUsePkce, codeChallenge, randomString } from '../spotify/pkce';
import {
  type ConnectOutcome,
  type MusicConnectBody,
  type MusicStatus,
  type PendingTidalConnect,
  tidalAuthorizeUrl,
} from './musicConnect';

// Web: a full-page redirect to Tidal and back to /tidal-callback, like Spotify (spotifyRedirect.ts). The native app
// uses expo-web-browser's auth session instead (tidalRedirect.native.ts).

const PENDING_KEY = 'fandom-score.tidal-connect';

export function callbackUri(): string {
  return `${window.location.origin}/tidal-callback`;
}

/**
 * Leaves the app for Tidal's consent page; /tidal-callback finishes the connect. Throws if this page can't do PKCE
 * (plain http on a LAN address). The `connect` argument is for the native flow, which finishes in place.
 */
export async function startTidalConnect(
  clientId: string,
  _connect?: (body: MusicConnectBody) => Promise<MusicStatus>,
): Promise<ConnectOutcome> {
  if (!canUsePkce()) {
    throw new Error('Connecting Tidal needs HTTPS (or http://127.0.0.1). Open the app through a tunnel or 127.0.0.1.');
  }
  const pending: PendingTidalConnect = { state: randomString(32), verifier: randomString(64), redirectUri: callbackUri() };
  const url = tidalAuthorizeUrl({
    clientId,
    redirectUri: pending.redirectUri,
    state: pending.state,
    challenge: await codeChallenge(pending.verifier),
  });
  sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));
  window.location.assign(url);
  return 'redirecting';
}

/** The attempt this tab started, removed as it is read so a code is only ever sent once. */
export function takePendingTidalConnect(): PendingTidalConnect | null {
  try {
    const value = sessionStorage.getItem(PENDING_KEY);
    sessionStorage.removeItem(PENDING_KEY);
    return value ? (JSON.parse(value) as PendingTidalConnect) : null;
  } catch {
    return null;
  }
}
