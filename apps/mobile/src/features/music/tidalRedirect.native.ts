import * as WebBrowser from 'expo-web-browser';

import { codeChallenge, queryParams, randomString } from '../spotify/spotifyRedirect.native';
import {
  completeTidalConnect,
  type ConnectOutcome,
  type MusicConnectBody,
  type MusicStatus,
  tidalAuthorizeUrl,
} from './musicConnect';

// iOS/Android: Tidal's consent page opens in an in-app browser session and returns to the app on the
// fandomscore:// redirect, like Spotify (spotifyRedirect.native.ts). The attempt stays in memory. The redirect must
// be registered in the TIDAL dashboard and listed in the API's MusicProviders__Tidal__RedirectUris.

export function callbackUri(): string {
  return 'fandomscore://tidal-callback';
}

/** Runs the whole connect flow and sends the code to the API through `connect`. */
export async function startTidalConnect(
  clientId: string,
  connect: (body: MusicConnectBody) => Promise<MusicStatus>,
): Promise<ConnectOutcome> {
  const pending = { state: randomString(32), verifier: randomString(64), redirectUri: callbackUri() };
  const url = tidalAuthorizeUrl({
    clientId,
    redirectUri: pending.redirectUri,
    state: pending.state,
    challenge: await codeChallenge(pending.verifier),
  });

  const result = await WebBrowser.openAuthSessionAsync(url, pending.redirectUri);
  if (result.type !== 'success') {
    // The fan closed the browser (cancel or dismiss); nothing was granted.
    return 'cancelled';
  }
  return completeTidalConnect(queryParams(result.url), pending, connect);
}

/** Native connects finish inside startTidalConnect; there is never a pending web redirect. */
export function takePendingTidalConnect(): null {
  return null;
}
