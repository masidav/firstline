# TODO — Authentication

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Confirm the authentication method and session/token strategy; document unresolved choices.
- [ ] Implement registration and login flows appropriate to the chosen authentication provider.
- [ ] Implement logout and invalidate/revoke sessions or refresh tokens as appropriate.
- [ ] Implement secure password handling if passwords are managed by this service.
- [ ] Implement account recovery and credential reset if applicable.
- [ ] Validate inputs and return non-sensitive error messages.
- [ ] Protect authenticated routes and reject missing, invalid, expired, or revoked credentials.
- [ ] Apply rate limits and abuse protection to login and recovery endpoints.
- [ ] Ensure tokens and credentials are never written to logs.
- [ ] Add tests for successful flows, invalid credentials, expiration, revocation, and authorization.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
