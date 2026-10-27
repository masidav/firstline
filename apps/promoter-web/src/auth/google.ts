import { config } from '../config';
import type { Credential } from './session';

// Google sign-in like the fan web app (apps/mobile/src/features/authentication): a full-page redirect to Google's
// account chooser, the ID token back in the URL fragment at /google-callback, checked against the state this tab
// started. No Google script runs on the page.

const PENDING_KEY = 'fandom-score-promoter.google-sign-in';

interface Pending {
  state: string;
  nonce: string;
}

export function randomString(length: number): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';
  return Array.from(crypto.getRandomValues(new Uint8Array(length)), (byte) => alphabet[byte % alphabet.length]).join('');
}

export function startGoogleSignIn(): void {
  const pending: Pending = { state: randomString(32), nonce: randomString(32) };
  sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));
  const params = new URLSearchParams({
    client_id: config.googleWebClientId,
    redirect_uri: `${window.location.origin}/google-callback`,
    response_type: 'id_token',
    scope: 'openid email profile',
    state: pending.state,
    nonce: pending.nonce,
    prompt: 'select_account',
  });
  window.location.assign(`https://accounts.google.com/o/oauth2/v2/auth?${params.toString()}`);
}

/** Reads Google's answer from the address (fragment or query) against the attempt this tab started. */
export function completeGoogleSignIn(url: string): { credential: Credential } | { error: string } {
  let pending: Pending | null = null;
  try {
    pending = JSON.parse(sessionStorage.getItem(PENDING_KEY) ?? 'null') as Pending | null;
  } catch {
    pending = null;
  }
  sessionStorage.removeItem(PENDING_KEY);
  const [beforeFragment, fragment = ''] = url.split('#', 2);
  const params = new URLSearchParams(`${beforeFragment?.split('?', 2)[1] ?? ''}&${fragment}`);
  if (params.get('error')) {
    return { error: params.get('error') === 'access_denied' ? "You didn't continue with Google." : `Google said: ${params.get('error')}.` };
  }
  const idToken = params.get('id_token');
  if (!pending || !idToken || params.get('state') !== pending.state) {
    return { error: "This Google sign-in expired or didn't start here. Try again." };
  }
  return { credential: { provider: 'google', idToken, nonce: pending.nonce } };
}
