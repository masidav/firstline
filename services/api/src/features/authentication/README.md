# Authentication

Sign in with Apple or Google, app-issued sessions, and rotating refresh tokens.
Design and reasoning: [ADR 0003](../../../../../docs/adr/0003-authentication.md).

## Endpoints (openapi tag Auth)
| Endpoint | Auth | Notes |
| --- | --- | --- |
| `POST /v1/auth/sessions` | none, rate limited | Identity token → session. `201` and a new account on first sign-in (needs `handle`); `200` after that. |
| `POST /v1/auth/refresh` | none, rate limited | Rotates the refresh token. Reusing an old one revokes the session. |
| `DELETE /v1/auth/sessions/current` | bearer | Ends this device's session. |

## Files
| File | Role |
| --- | --- |
| `AuthSetup.cs` | DI: options, JWT bearer validation with a live-session check, rate limiter. `UserId()`/`SessionId()` claim helpers. |
| `AuthEndpoints.cs` | The three endpoints; account creation on first sign-in. |
| `SessionService.cs` | Sessions, refresh-token rotation and reuse detection, access-token issuing. |
| `IdentityTokenValidator.cs` | Checks Apple/Google identity tokens against their OpenID configuration (`IIdentityTokenValidator`). |
| `AuthOptions.cs` | `Auth` configuration section. |

Tables: `auth_identities`, `auth_sessions`, `refresh_tokens` (`database/migrations/0002_auth_sessions.sql`).

## Configuration
`Auth__SigningKey` (required secret), `Auth__Apple__ClientIds`, `Auth__Google__ClientIds`. See `.env.example`
and the ADR. With no client IDs set, sign-in returns `422` for that provider; tests replace the validator.

## Tests
- `tests/FandomScore.Tests/AuthApiTests.cs`: sign-in, account creation rules, conflicts, token checks, rotation
  and reuse, expiry, sign-out, account deletion, and that no credential is logged. Real PostgreSQL, fake provider.
- `tests/FandomScore.Tests/OidcIdentityTokenValidatorTests.cs`: the real token checks (signature, issuer, audience,
  expiry, nonce, `email_verified`) with a local RSA key.
- `tests/FandomScore.Tests/RateLimitTests.cs`: `429 rate_limited` with `Retry-After`.
- `database/tests/invariants.sql` (auth section): one live refresh token per session, permanent revocation,
  deletion cleanup, no sessions for deleted users.

## Known limitations
- One email per account, by decision (2026-10-18): accounts are not linked across providers, so the same email from
  another provider gets `409 email_in_use`.
- Apple server-to-server notifications and Apple token revocation on account deletion are not done yet.
- Concurrent refreshes with one token end the session; the app must serialize refreshes.
- Rate limiting is per connection IP until forwarded headers are configured for the GKE load balancer.
