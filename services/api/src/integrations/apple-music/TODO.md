# TODO — Apple Music integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Confirm MusicKit API access, developer token handling, and terms for using listening data to decide eligibility.
- [ ] Implement user authorization (Music User Token) and secure token storage like the Spotify connection.
- [ ] Map Apple Music library and recently played data to tracked artists only.
- [ ] Handle token expiry, revoked access, and rate limits.
- [ ] Add mocked integration tests and a manual verification checklist.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
