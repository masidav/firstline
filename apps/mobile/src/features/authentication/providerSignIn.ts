import { ApiError } from '../../api/client';
import { errorMessage } from '../../api/errors';
import type { Credential } from '../../auth/session';

// Sign-in with Google or Apple, the parts that are the same on every platform. The app gets an identity token
// from the provider and sends it to POST /auth/sessions (docs/adr/0003-authentication.md); the API checks the
// signature, the audience (our client IDs), and the nonce. The web uses a full-page redirect to Google and back
// (googleSignIn.ts); the native app opens an auth session (googleSignIn.native.ts) or the Apple sheet (appleSignIn.ts).

export type SignInResult = { ok: true; credential: Credential } | { ok: false; message: string };

/**
 * What starting a sign-in leads to: on the web the page leaves for the provider (`redirecting`) and the callback
 * route finishes; in the native app the flow finishes in place, unless the fan backs out (`cancelled`).
 */
export type SignInOutcome = SignInResult | 'redirecting' | 'cancelled';

/** Kept between leaving for Google and coming back to the callback route. */
export interface PendingGoogleSignIn {
  state: string;
  nonce: string;
  redirectUri: string;
  /** PKCE verifier, for the code flow (native). */
  verifier?: string;
}

export const GOOGLE_SCOPES = 'openid email profile';

/**
 * Google's OpenID Connect authorization URL. The web asks for the ID token directly (`id_token`, returned in the
 * URL fragment, which never reaches a server); the native app asks for a code and exchanges it with PKCE.
 */
export function googleAuthorizeUrl(options: {
  clientId: string;
  redirectUri: string;
  state: string;
  nonce: string;
  response: { type: 'id_token' } | { type: 'code'; challenge: string };
}): string {
  const params = new URLSearchParams({
    client_id: options.clientId,
    redirect_uri: options.redirectUri,
    response_type: options.response.type,
    scope: GOOGLE_SCOPES,
    state: options.state,
    nonce: options.nonce,
    prompt: 'select_account',
  });
  if (options.response.type === 'code') {
    params.set('code_challenge_method', 'S256');
    params.set('code_challenge', options.response.challenge);
  }
  return `https://accounts.google.com/o/oauth2/v2/auth?${params.toString()}`;
}

export interface GoogleRedirectParams {
  id_token?: string;
  code?: string;
  state?: string;
  error?: string;
}

/** The redirect's fragment and query (#id_token=&state=, ?code=&state=, or ?error=), parsed by hand: React Native's URL support is partial. */
export function parseGoogleRedirect(url: string): GoogleRedirectParams {
  const [beforeFragment, fragment = ''] = url.split('#', 2);
  const query = beforeFragment?.split('?', 2)[1] ?? '';
  const params: Record<string, string> = {};
  for (const pair of `${query}&${fragment}`.split('&')) {
    const [key, value = ''] = pair.split('=');
    if (key) params[decodeURIComponent(key)] = decodeURIComponent(value.replace(/\+/g, ' '));
  }
  return { id_token: params.id_token, code: params.code, state: params.state, error: params.error };
}

/**
 * Handles Google's redirect back: checks the state matches the attempt this app started (so another site can't
 * sign us in to an account of its choosing), then returns the credential for POST /auth/sessions.
 * `exchange` turns a code into an ID token (native only).
 */
export async function completeGoogleSignIn(
  params: GoogleRedirectParams,
  pending: PendingGoogleSignIn | null,
  exchange?: (code: string, pending: PendingGoogleSignIn) => Promise<string>,
): Promise<SignInResult> {
  if (params.error) {
    return {
      ok: false,
      message: params.error === 'access_denied' ? "You didn't continue with Google." : `Google said: ${params.error}.`,
    };
  }
  if (!pending || !params.state || params.state !== pending.state) {
    return { ok: false, message: "This Google sign-in expired or didn't start here. Try again." };
  }

  try {
    const idToken = params.id_token ?? (params.code && exchange ? await exchange(params.code, pending) : null);
    if (!idToken) {
      return { ok: false, message: "Google didn't return a sign-in. Try again." };
    }
    return { ok: true, credential: { provider: 'google', idToken, nonce: pending.nonce } };
  } catch {
    return { ok: false, message: "Couldn't finish signing in with Google. Try again." };
  }
}

/** A message for a failed POST /auth/sessions. */
export function signInErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 401) {
      return 'That sign-in expired. Try again.';
    }
    if (error.fieldError('provider')) {
      return "This way of signing in isn't available right now.";
    }
    if (error.code === 'validation_failed' && !error.problem.errors?.length && error.problem.detail) {
      // For example "The sign-in provider did not share a verified email address."
      return error.problem.detail;
    }
  }
  return errorMessage(error);
}

const HANDLE_CHARACTERS = /[^A-Za-z0-9_]/g;

/**
 * A starting point for the handle of a new account, from the token's email (the part before the @) or name.
 * The token is decoded without verification: the API verifies it, this only prefills a text field.
 */
export function suggestHandle(idToken: string): string {
  const payload = idToken.split('.')[1];
  if (!payload) {
    return '';
  }
  try {
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const claims = JSON.parse(decodeBase64(base64)) as { email?: unknown; name?: unknown };
    const source = typeof claims.email === 'string' ? claims.email.split('@')[0] : claims.name;
    if (typeof source !== 'string') {
      return '';
    }
    const cleaned = source.replace(HANDLE_CHARACTERS, '_').replace(/^_+|_+$/g, '').slice(0, 30);
    return cleaned.length >= 3 ? cleaned : '';
  } catch {
    return '';
  }
}

function decodeBase64(base64: string): string {
  const binary = atob(base64);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}
