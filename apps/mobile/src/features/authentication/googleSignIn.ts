import { config } from '../../config';
import { randomString } from './crypto';
import { googleAuthorizeUrl, type PendingGoogleSignIn, type SignInOutcome } from './providerSignIn';

// Web: a full-page redirect to Google and back to /google-callback, where the ID token arrives in the URL
// fragment. (Popups are unreliable in a home-screen web app on iOS, and this needs no Google script on the page,
// which matters while the refresh token lives in localStorage.) The native app uses an auth session instead
// (googleSignIn.native.ts). The redirect URI must be registered on the Google OAuth web client (see README).

const PENDING_KEY = 'fandom-score.google-sign-in';

/** The OAuth client this platform signs in with; empty hides the button. */
export const googleClientId = config.googleWebClientId;

export function callbackUri(): string {
  return `${window.location.origin}/google-callback`;
}

/** Leaves the app for Google's account chooser; /google-callback finishes the sign-in. */
export async function startGoogleSignIn(): Promise<SignInOutcome> {
  const pending: PendingGoogleSignIn = { state: randomString(32), nonce: randomString(32), redirectUri: callbackUri() };
  const url = googleAuthorizeUrl({
    clientId: googleClientId,
    redirectUri: pending.redirectUri,
    state: pending.state,
    nonce: pending.nonce,
    response: { type: 'id_token' },
  });
  sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));
  window.location.assign(url);
  return 'redirecting';
}

/** The attempt this tab started, removed as it is read so a token is only ever taken once. */
export function takePendingGoogleSignIn(): PendingGoogleSignIn | null {
  try {
    const value = sessionStorage.getItem(PENDING_KEY);
    sessionStorage.removeItem(PENDING_KEY);
    return value ? (JSON.parse(value) as PendingGoogleSignIn) : null;
  } catch {
    return null;
  }
}
