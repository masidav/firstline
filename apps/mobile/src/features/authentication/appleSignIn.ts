import * as AppleAuthentication from 'expo-apple-authentication';
import Constants from 'expo-constants';

import { randomString, sha256Hex } from './crypto';
import type { SignInOutcome } from './providerSignIn';

// Sign in with Apple: iOS shows Apple's sheet and hands back an identity token. On the web and Android the module
// reports itself unavailable and the button is not shown (Apple's web flow needs a Services ID and a server-side
// form post, neither of which exists yet; see the README).

/**
 * True on an iPhone or iPad with Sign in with Apple; false on the web and Android, and in a build made without the
 * entitlement (a personal-team build, app.config.ts), where Apple's sheet would open and then fail.
 */
export function isAppleSignInAvailable(): Promise<boolean> {
  if (appleSignInConfigured() === false) return Promise.resolve(false);
  return AppleAuthentication.isAvailableAsync().catch(() => false);
}

function appleSignInConfigured(): boolean | undefined {
  return (Constants.expoConfig?.extra as { appleSignIn?: boolean } | undefined)?.appleSignIn;
}

/** Shows Apple's sheet and returns the credential for POST /auth/sessions. */
export async function startAppleSignIn(): Promise<SignInOutcome> {
  // Apple puts the SHA-256 of what it is given into the token; the API hashes the raw nonce to compare.
  const nonce = randomString(32);
  try {
    const credential = await AppleAuthentication.signInAsync({
      requestedScopes: [AppleAuthentication.AppleAuthenticationScope.FULL_NAME, AppleAuthentication.AppleAuthenticationScope.EMAIL],
      nonce: await sha256Hex(nonce),
    });
    if (!credential.identityToken) {
      return { ok: false, message: "Apple didn't return a sign-in. Try again." };
    }
    return { ok: true, credential: { provider: 'apple', idToken: credential.identityToken, nonce } };
  } catch (error) {
    if (isCancelled(error)) {
      return 'cancelled';
    }
    return { ok: false, message: "Couldn't sign in with Apple. Try again." };
  }
}

export type AppleReauthentication = { code: string } | 'cancelled' | 'failed';

/**
 * Before deleting an account that signs in with Apple: a fresh authorization code, which the API uses to revoke
 * the app's Apple sign-in (App Store requirement). `failed` lets deletion go ahead without it, as the API allows.
 */
export async function reauthenticateWithApple(): Promise<AppleReauthentication> {
  try {
    const credential = await AppleAuthentication.signInAsync();
    return credential.authorizationCode ? { code: credential.authorizationCode } : 'failed';
  } catch (error) {
    return isCancelled(error) ? 'cancelled' : 'failed';
  }
}

function isCancelled(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { code?: unknown }).code === 'ERR_REQUEST_CANCELED';
}
