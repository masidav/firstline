# TODO — Spotify provider integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Verify current Spotify API access, endpoints, scopes, rate limits, and application-mode requirements.
- [ ] Implement OAuth authorization and callback validation using the selected auth pattern.
- [ ] Request only scopes needed by the supported product features.
- [ ] Encrypt refresh tokens application-side using the configured key-management approach.
- [ ] Implement provider clients with timeouts, safe retries, and rate-limit handling.
- [ ] Normalize provider responses into internal types without storing untracked artists' listening data.
- [ ] Add tests using mocked provider responses and documented integration checks.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
