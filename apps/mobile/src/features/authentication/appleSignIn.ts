import { config } from '../../config';
import { randomString } from './crypto';
import {
  type AppleReauthentication,
  appleAuthorizeUrl,
  appleState,
  type PendingAppleSignIn,
  type SignInOutcome,
} from './providerSignIn';

// Web: Sign in with Apple by a full-page redirect, like Google (googleSignIn.ts), with one difference. Apple only
// returns the identity token with the email through a form POST, so it posts to the API (POST /v1/auth/apple/callback),
// which sends the browser on to /apple-callback with the token in the URL fragment. The `state` carries this app's
// origin for that redirect plus a random part only this tab knows. The native app shows Apple's sheet instead
// (appleSignIn.native.ts). Needs a Services ID (config.appleServicesId) whose return URL is the API's callback (README).

export type { AppleReauthentication };

const PENDING_KEY = 'fandom-score.apple-sign-in';

/** True when a Services ID is configured; empty hides the button. */
export function isAppleSignInAvailable(): Promise<boolean> {
  return Promise.resolve(Boolean(config.appleServicesId));
}

/** Where Apple posts the result: the API, registered as the Services ID's return URL. */
export function appleCallbackUri(): string {
  return `${apiOrigin()}/v1/auth/apple/callback`;
}

/** config.apiUrl (`https://api.example/v1`) without its `/v1`. */
function apiOrigin(): string {
  return config.apiUrl.replace(/\/+$/, '').replace(/\/v1$/, '');
}

/** Leaves the app for Apple's sign-in page; /apple-callback finishes the sign-in. */
export async function startAppleSignIn(): Promise<SignInOutcome> {
  // On the web Apple puts the nonce in the token as given (the API accepts it raw or hashed), so it goes as is.
  const pending: PendingAppleSignIn = { state: appleState(window.location.origin, randomString(32)), nonce: randomString(32) };
  const url = appleAuthorizeUrl({
    servicesId: config.appleServicesId,
    redirectUri: appleCallbackUri(),
    state: pending.state,
    nonce: pending.nonce,
  });
  sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));
  window.location.assign(url);
  return 'redirecting';
}

/** The attempt this tab started, removed as it is read so a token is only ever taken once. */
export function takePendingAppleSignIn(): PendingAppleSignIn | null {
  try {
    const value = sessionStorage.getItem(PENDING_KEY);
    sessionStorage.removeItem(PENDING_KEY);
    return value ? (JSON.parse(value) as PendingAppleSignIn) : null;
  } catch {
    return null;
  }
}

/**
 * Before deleting an account on the web: Apple's web flow can't hand back a fresh authorization code in place, so
 * deletion goes ahead without one, which the API allows (session.deleteAccount).
 */
export function reauthenticateWithApple(): Promise<AppleReauthentication> {
  return Promise.resolve('failed');
}
