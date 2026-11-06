# Authentication

Sign in with Apple or Google, app-issued sessions, and rotating refresh tokens.
Design and reasoning: [ADR 0003](../../../../../docs/adr/0003-authentication.md).

## Endpoints (openapi tag Auth)
| Endpoint | Auth | Notes |
| --- | --- | --- |
| `POST /v1/auth/sessions` | none, rate limited | Identity token → session. `201` and a new account on first sign-in (needs `handle`); `200` after that. |
| `POST /v1/auth/refresh` | none, rate limited | Rotates the refresh token. Reusing an old one revokes the session. |
| `DELETE /v1/auth/sessions/current` | bearer | Ends this device's session. |
| `POST /v1/webhooks/apple` | Apple-signed JWT | Sign in with Apple notifications: `consent-revoked` signs out everywhere, `account-delete` deletes the account. |
| `POST /v1/auth/apple/callback` | none, rate limited | Sign in with Apple on the web (2026-11-05): Apple's `form_post` lands here and is handed to the app at `<origin>/apple-callback#id_token=…&state=…`; the origin comes from `state` and must be in `Cors__AllowedOrigins`. Nothing is validated or stored; the app finishes with `POST /auth/sessions`. |

## Files
| File | Role |
| --- | --- |
| `AuthSetup.cs` | DI: options, JWT bearer validation with a live-session check, staff policy (`StaffPolicy`), rate limiter. `UserId()`/`SessionId()` claim helpers. |
| `AuthEndpoints.cs` | The three endpoints; account creation on first sign-in. |
| `SessionService.cs` | Sessions, refresh-token rotation and reuse detection, access-token issuing. |
| `AppleClient.cs` | Revokes the Apple sign-in on account deletion (code exchange, then revoke). |
| `AppleNotificationEndpoints.cs` | `POST /v1/webhooks/apple`. |
| `AppleWebEndpoints.cs` | `POST /v1/auth/apple/callback`: the web relay (303 with the token in the fragment; origin allow-listed from `state`). |
| `IdentityTokenValidator.cs` | Checks Apple/Google identity tokens against their OpenID configuration (`IIdentityTokenValidator`). |
| `AuthOptions.cs` | `Auth` configuration section. |

Tables: `auth_identities`, `auth_sessions`, `refresh_tokens` (`database/migrations/0002_auth_sessions.sql`, `0003_session_provider_revoked.sql`).

## Configuration
`Auth__SigningKey` (required secret), `Auth__Apple__ClientIds`, `Auth__Google__ClientIds`, and for Apple revocation
`Auth__Apple__TeamId`, `Auth__Apple__KeyId`, `Auth__Apple__PrivateKey` (secret). See `.env.example`
and the ADR. With no client IDs set, sign-in returns `422` for that provider; tests replace the validator.
For Apple on the web: `Auth__Apple__ClientIds` must include the Services ID, `Cors__AllowedOrigins` the web app's
origin, and the Services ID's return URL in the Apple Developer account is `{App__PublicOrigin}/v1/auth/apple/callback`
(Apple verifies the domain).

## Tests
- `tests/FandomScore.Tests/AuthApiTests.cs`: sign-in (a nonce is required), account creation rules, conflicts, token checks, rotation
  and reuse, expiry, sign-out, account deletion, and that no credential is logged. Real PostgreSQL, fake provider.
- `tests/FandomScore.Tests/OidcIdentityTokenValidatorTests.cs`: the real token checks (signature, issuer, audience,
  expiry, nonce, `email_verified`) with a local RSA key.
- `tests/FandomScore.Tests/RateLimitTests.cs`: `429 rate_limited` with `Retry-After`.
- `tests/FandomScore.Tests/AppleTests.cs`: real Apple token validation, deletion revoking the Apple sign-in (fake Apple API),
  notifications, forged notifications.
- `tests/FandomScore.Tests/StaffAuthorizationTests.cs`: 401/403/200 and immediate removal of staff access.
- `database/tests/invariants.sql` (auth section): one live refresh token per session, permanent revocation,
  deletion cleanup, no sessions for deleted users.

## Known limitations
- One email per account, by decision (2026-10-18): accounts are not linked across providers, so the same email from
  another provider gets `409 email_in_use`.
- Apple revocation is best effort and relies on the app sending a fresh authorization code; no retry yet.
- Concurrent refreshes with one token end the session; the app must serialize refreshes.
- Rate limiting is per connection IP until forwarded headers are configured for the GKE load balancer.
