# Authentication mobile UI

## Purpose
The sign-in screen and everything that gets a provider identity token to `POST /v1/auth/sessions`
(`docs/adr/0003-authentication.md`). Session handling itself is in `src/auth/`.

## Files
| File | Role |
| --- | --- |
| `SignInScreen.tsx` | Continue with Google, Continue with Apple (iOS, and the web with a Services ID), developer sign-in when enabled. |
| `FinishSignIn.tsx` | Sends the credential to the API; asks a new account for a handle (prefilled from the token's email). |
| `providerSignIn.ts` | Platform-neutral pieces: Google's and Apple's authorize URLs, redirect parsing, state checks, error messages. |
| `googleSignIn.ts` | Web: full-page redirect to Google, ID token back in the fragment at `/google-callback`. |
| `googleSignIn.native.ts` | iOS/Android: auth session, code exchanged with PKCE against the iOS client (no secret). |
| `appleSignIn.ts` | Web: full-page redirect to Apple, which posts to the API (`POST /v1/auth/apple/callback`); the API sends the token on in the fragment at `/apple-callback`. Raw nonce. |
| `appleSignIn.native.ts` | iOS: `expo-apple-authentication`, sign-in with a hashed nonce; re-authentication before account deletion. |
| `AppleSignInButton(.native).tsx` | Apple's own button on iOS; a black Continue with Apple button on the web. |
| `DevSignInForm.tsx` | Developer sign-in (`dev:<name>` tokens, Development API only). |
| `crypto.ts` | Random strings, SHA-256 hex, PKCE challenge (expo-crypto). |

## Setup
See `apps/mobile/README.md`, "Sign in with Google" and "Sign in with Apple": client IDs go in
`EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID` / `EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID` and the API's `Auth__Google__ClientIds`
/ `Auth__Apple__ClientIds`.

Apple on the web needs a **Services ID** (`EXPO_PUBLIC_APPLE_SERVICES_ID`, or `APPLE_SERVICES_ID` in the hosted
image's environment; empty hides the button). In the Apple developer portal the Services ID must have Sign in with
Apple enabled, the API's domain verified, and the return URL `{API origin}/v1/auth/apple/callback` registered (the
API's `App__PublicOrigin`; Apple accepts HTTPS only). The API's `Auth__Apple__ClientIds` must include the Services ID
next to the bundle ID, and `Cors__AllowedOrigins` the app's origin, which travels in the `state` so the API knows
where to send the browser back. The web sends the raw nonce: Apple echoes it unhashed in the token there, and the
API accepts raw or SHA-256.

## Known limitations
- Deleting an account on the web can't get a fresh Apple authorization code (Apple's web flow is a redirect, not a
  sheet), so the deletion goes ahead without revoking the Apple sign-in, which the API allows.
- The native flows (Apple, Google with PKCE) are unit-tested with the modules mocked, not yet run on a device; the
  Apple web flow is unit-tested but not yet tried against a real Services ID.
- Google only redirects to `https` or `http://localhost`, so testing on a phone needs an HTTPS tunnel.
