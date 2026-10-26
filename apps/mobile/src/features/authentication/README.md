# Authentication mobile UI

## Purpose
The sign-in screen and everything that gets a provider identity token to `POST /v1/auth/sessions`
(`docs/adr/0003-authentication.md`). Session handling itself is in `src/auth/`.

## Files
| File | Role |
| --- | --- |
| `SignInScreen.tsx` | Continue with Google, Continue with Apple (iOS only), developer sign-in when enabled. |
| `FinishSignIn.tsx` | Sends the credential to the API; asks a new account for a handle (prefilled from the token's email). |
| `providerSignIn.ts` | Platform-neutral pieces: Google's authorize URL, redirect parsing, state check, error messages. |
| `googleSignIn.ts` | Web: full-page redirect to Google, ID token back in the fragment at `/google-callback`. |
| `googleSignIn.native.ts` | iOS/Android: auth session, code exchanged with PKCE against the iOS client (no secret). |
| `appleSignIn.ts` | `expo-apple-authentication`: sign-in with a hashed nonce; re-authentication before account deletion. |
| `AppleSignInButton(.native).tsx` | Apple's own button on iOS; nothing on the web. |
| `DevSignInForm.tsx` | Developer sign-in (`dev:<name>` tokens, Development API only). |
| `crypto.ts` | Random strings, SHA-256 hex, PKCE challenge (expo-crypto). |

## Setup
See `apps/mobile/README.md`, "Sign in with Google" and "Sign in with Apple": client IDs go in
`EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID` / `EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID` and the API's `Auth__Google__ClientIds`
/ `Auth__Apple__ClientIds`.

## Known limitations
- Apple on the web is not offered (needs a Services ID and a server-side form post).
- The native flows (Apple, Google with PKCE) are unit-tested with the modules mocked, not yet run on a device.
- Google only redirects to `https` or `http://localhost`, so testing on a phone needs an HTTPS tunnel.
