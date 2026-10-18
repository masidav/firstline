# TODO — Authentication

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Confirm the authentication method and session/token strategy; document unresolved choices. ([ADR 0003](../../../../../docs/adr/0003-authentication.md))
- [x] Implement registration and login flows appropriate to the chosen authentication provider. (`POST /v1/auth/sessions`, Apple and Google)
- [x] Implement logout and invalidate/revoke sessions or refresh tokens as appropriate. (sign-out, rotation, reuse detection)
- [x] Implement secure password handling if passwords are managed by this service. *N/A: no passwords (provider sign-in only).*
- [x] Implement account recovery and credential reset if applicable. *N/A: recovery is signing in again with Apple/Google.*
- [x] Validate inputs and return non-sensitive error messages.
- [x] Protect authenticated routes and reject missing, invalid, expired, or revoked credentials. (all of `/v1` unless `AllowAnonymous`)
- [x] Apply rate limits and abuse protection to login and recovery endpoints. (20/min per IP; forwarded headers still needed behind GKE)
- [x] Ensure tokens and credentials are never written to logs. (`Credentials_are_never_logged`)
- [x] Add tests for successful flows, invalid credentials, expiration, revocation, and authorization.
- [x] Handle Sign in with Apple server-to-server notifications (consent revoked, email changed, account deleted). (`POST /v1/webhooks/apple`, AppleTests)
- [x] Revoke the Apple token when an account is deleted. (code sent with `DELETE /me`; AppleTests with a fake Apple API)
- [ ] Retry failed Apple revocations once the job queue exists (Phase 3).
- [ ] Register the notification URL and create the Sign in with Apple key in the Apple Developer account; store the key in Secret Manager.
- [x] ~~Account linking~~ *Decided 2026-10-18: one email per account, no linking (`409 email_in_use`).*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-18: `dotnet test` 27/27 pass (AuthApiTests, OidcIdentityTokenValidatorTests, RateLimitTests, invariants); a deliberately broken reuse check made `Refresh_rotates_tokens_and_reuse_revokes_the_session` fail.
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
