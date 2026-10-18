# TODO — Spotify provider integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Verify current Spotify API access, endpoints, scopes, rate limits, and application-mode requirements.
      Checked 2026-10-18 against Spotify's February and July 2026 changelogs; see ADR 0005.
- [x] Implement OAuth authorization and callback validation using the selected auth pattern (PKCE, server-side exchange, redirect URI allowlist).
- [x] Request only scopes needed by the supported product features (five read scopes, ADR 0005).
- [x] Encrypt refresh tokens application-side (AES-256-GCM, `Spotify__TokenKey`, rotation via `PreviousTokenKeys`). Cloud KMS envelope encryption is a later option.
- [x] Implement provider clients with timeouts, safe retries, and rate-limit handling.
- [x] Normalize provider responses into internal types without storing untracked artists' listening data.
- [x] Add tests using mocked provider responses (`FakeSpotify`).
- [ ] Documented integration check against real Spotify with a development-mode app and an allowlisted account.

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-18: `dotnet test` 67/67 passed (SpotifyTokenProtectorTests 4, SpotifyApiTests 9, SpotifySyncJobTests 9, plus invariants).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
