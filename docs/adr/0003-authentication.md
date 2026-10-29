# ADR 0003: Authentication with Apple and Google sign-in, app-issued sessions

- **Status:** Accepted (2026-10-18). Implemented in Phase 2.
- **Code:** `services/api/src/features/authentication`, `database/migrations/0002_auth_sessions.sql`.
- **Contract:** `openapi.yaml`, tag Auth (`POST /auth/sessions`, `POST /auth/refresh`, `DELETE /auth/sessions/current`).

## Decision

| Concern | Choice |
| --- | --- |
| How fans prove who they are | **Sign in with Apple** or **Google**. The app gets an OpenID Connect identity token from the provider and sends it to `POST /auth/sessions`. No passwords, so no password storage, resets, or credential stuffing. |
| Checking identity tokens | The API checks the token's signature against the provider's published keys (fetched from its OpenID configuration and cached; refetched when a key ID is unknown). It also checks the issuer, that the audience is one of our client IDs, the expiry, and the nonce: every sign-in must send the raw nonce the app put in the provider request, and the token must carry it (raw for Google, SHA-256 hex for Apple), or the answer is `401` (required since 2026-10-29). |
| Account key | `auth_identities (provider, subject)` → `users.id`. The provider's `sub` is stable; emails can change, so they are not used to find accounts. |
| New accounts | Need a handle and a **verified** email from the provider. Apple's private relay emails are accepted. |
| Access tokens | JWT, HS256, **15 minutes**, claims `sub` (user) and `sid` (session). Signed with `Auth__SigningKey` from the environment. |
| Session check | Every authenticated request checks that the session is still active (one indexed query). So signing out and account deletion take effect immediately, not after up to 15 minutes. |
| Refresh tokens | 256-bit random, opaque, stored only as SHA-256. **Rotated on every use.** Presenting a used token revokes the whole session (reuse detection). Unused tokens expire after **30 days**, and a session ends after **180 days** at most. |
| Sign-out | Ends the calling device's session only. |
| Account deletion | `DELETE /me` sets `users.deleted_at`. Triggers scrub personal fields (0001), and delete sessions, refresh tokens, and provider identities (0002), so the same Apple/Google account can start over as a new user. |
| Apple account deletion | We store no Apple tokens. When an Apple user deletes their account, the app sends a fresh Sign in with Apple authorization code with `DELETE /me`; the API exchanges it (client secret: ES256 JWT signed with our Sign in with Apple key), checks it is the same Apple account, and revokes the resulting token. Best effort: deletion never waits on Apple succeeding. |
| Apple notifications | `POST /webhooks/apple`, verified with Apple's signing keys. `consent-revoked` ends all the user's sessions (`revoked_reason = provider_revoked`, migration 0003); `account-delete` deletes the account, since it can never be signed in to again; email relay events change nothing. |
| Staff access | Policy `AuthSetup.StaffPolicy` for `/admin` routes: reads `is_staff` from the database on each request, so removing staff access is immediate. |
| Developer sign-in | `Auth__DevSignIn=true` accepts Google sign-ins whose token is `dev:<name>` without checking them (account `dev:<name>`, email `<name>@example.test`), so the app can be tried without provider accounts. The API refuses to start with it outside the Development environment. |
| Browser clients | CORS for the origins in `Cors__AllowedOrigins` (the Expo web build). Bearer tokens only, no cookies. |
| Abuse limits | Sign-in and refresh: 20 requests per minute per client IP (ASP.NET Core rate limiter). Cloud Armor adds edge limits in Phase 12. |

## Why
- **Provider sign-in:** fans already have Apple or Google accounts. App Store rules require Sign in with Apple
  (or an equivalent privacy-focused option) when other social logins are offered; check current guidelines before release. Bots must also get past the providers' own account checks.
- **Our own sessions instead of the provider's tokens:** we control lifetime, revocation, and reuse detection,
  and the rest of the API doesn't depend on which provider a fan used.
- **HS256 for now:** one service both signs and checks tokens, so a shared secret is enough. If another service
  needs to verify tokens (workers, promoter web), switch to an asymmetric key in Cloud KMS or Secret Manager,
  and publish the public key (JWKS).
- **Database session check per request:** costs one indexed query, and in exchange revocation is immediate.
  Revisit with a short-lived cache if it shows up in load tests.

## Consequences and known limits
- **Two refreshes at once from the same device** (e.g. app resume and a background task) look like token reuse
  and end the session. The app must serialize refreshes and store the new refresh token before using it.
  We can add a short grace window if this shows up in practice.
- **One provider per account.** Signing in with Google using an email that already belongs to an Apple account gets
  `409 email_in_use`. Decided 2026-10-18: one email per account, no account linking.
- **Apple revocation depends on the app** sending a fresh authorization code with the deletion request. If it is
  missing or Apple fails, the deletion still happens and a warning is logged (retry needs the job queue).
- **Client IP behind the load balancer:** the rate limiter uses the connection's IP. Before going live behind GKE,
  configure forwarded headers to trust only the load balancer (infrastructure TODO).
- Identity tokens, access tokens, and refresh tokens are never logged. A test checks this.

## Configuration
| Variable | Meaning |
| --- | --- |
| `Auth__SigningKey` | Secret. Base64 of at least 32 random bytes (`openssl rand -base64 32`). The API refuses to start without it. |
| `Auth__Apple__ClientIds` | Comma-separated: iOS bundle ID, Services ID. Empty disables Apple sign-in. |
| `Auth__Apple__TeamId`, `Auth__Apple__KeyId`, `Auth__Apple__PrivateKey` | Sign in with Apple key from the Apple Developer account, for revocation. The private key (`.p8` PEM) is a secret; a malformed key stops startup. The first Apple client ID (the iOS bundle ID) is used for revocation. |
| `Auth__Google__ClientIds` | Comma-separated: iOS, Android, and web OAuth client IDs. Empty disables Google sign-in. |
| `Auth__AccessTokenLifetime`, `Auth__RefreshTokenLifetime`, `Auth__SessionLifetime`, `Auth__PermitsPerMinute` | Optional overrides of the defaults above. |

Rotating `Auth__SigningKey` doesn't sign anyone out: current access tokens stop working, and the app gets new
ones with its refresh token (refresh tokens don't depend on the key).
