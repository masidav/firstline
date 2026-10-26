import * as WebBrowser from 'expo-web-browser';

import { config } from '../../config';
import { codeChallenge, randomString } from './crypto';
import {
  completeGoogleSignIn,
  googleAuthorizeUrl,
  parseGoogleRedirect,
  type PendingGoogleSignIn,
  type SignInOutcome,
} from './providerSignIn';

// iOS/Android: Google's account chooser opens in an in-app browser session (ASWebAuthenticationSession on iOS),
// which returns to the app on the client's own URL scheme with a code. The app exchanges the code for an ID token
// with PKCE; iOS/Android OAuth clients have no secret, so nothing sensitive is in the app. The attempt stays in
// memory, so nothing is stored. The scheme must be listed in app.json (`scheme`) for the redirect to open the app.

/** The OAuth client this platform signs in with; empty hides the button. */
export const googleClientId = config.googleIosClientId;

/** Google's required redirect for iOS clients: the client ID reversed, e.g. com.googleusercontent.apps.123-abc. */
export function callbackUri(clientId = googleClientId): string {
  const [id, ...domain] = clientId.split('.');
  return `${domain.reverse().join('.')}.${id}:/oauth2redirect`;
}

/** Runs the whole sign-in flow and returns the credential for POST /auth/sessions. */
export async function startGoogleSignIn(): Promise<SignInOutcome> {
  const pending: PendingGoogleSignIn = {
    state: randomString(32),
    nonce: randomString(32),
    verifier: randomString(64),
    redirectUri: callbackUri(),
  };
  const url = googleAuthorizeUrl({
    clientId: googleClientId,
    redirectUri: pending.redirectUri,
    state: pending.state,
    nonce: pending.nonce,
    response: { type: 'code', challenge: await codeChallenge(pending.verifier ?? '') },
  });

  const result = await WebBrowser.openAuthSessionAsync(url, pending.redirectUri);
  if (result.type !== 'success') {
    // The fan closed the browser (cancel or dismiss); nothing was granted.
    return 'cancelled';
  }
  return completeGoogleSignIn(parseGoogleRedirect(result.url), pending, exchangeCode);
}

/** Native sign-ins finish inside startGoogleSignIn; there is never a pending web redirect. */
export function takePendingGoogleSignIn(): null {
  return null;
}

/** Trades the code for an ID token at Google's token endpoint (no client secret: PKCE proves it is the same app). */
export async function exchangeCode(code: string, pending: PendingGoogleSignIn): Promise<string> {
  const response = await fetch('https://oauth2.googleapis.com/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      client_id: googleClientId,
      code,
      code_verifier: pending.verifier ?? '',
      grant_type: 'authorization_code',
      redirect_uri: pending.redirectUri,
    }).toString(),
  });
  if (!response.ok) {
    throw new Error(`Google token exchange failed (${response.status})`);
  }
  const body = (await response.json()) as { id_token?: string };
  if (!body.id_token) {
    throw new Error('Google token exchange returned no ID token');
  }
  return body.id_token;
}
