# TODO — Apple Music integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

Built 2026-11-05 as `packages/music-providers` (with Tidal), `services/api/src/features/music-sync`, `workers/music-sync`,
migration 0058; design in [ADR 0007](../../../../../docs/adr/0007-music-providers.md).

## Implementation checklist
- [ ] Confirm MusicKit API access, developer token handling, and terms for using listening data to decide eligibility.
      *Developer tokens: done (ES256 with the MusicKit key, `GET /me/music/apple_music/developer-token`). Access and terms: owner.*
- [x] Implement user authorization (Music User Token) and secure token storage like the Spotify connection.
      *`PUT /me/music/apple_music`; the token is checked with `me/storefront` and stored AES-GCM under its own purpose (2026-11-05).*
- [x] Map Apple Music library and recently played data to tracked artists only.
      *`MusicMatcher`: artist by name, track by ISRC then title, album by UPC then title; codes learned into the catalog. Heavy rotation ranks the artist; recently played counts (no times).*
- [x] Handle token expiry, revoked access, and rate limits. *401/403 → `reauth_required`; 429 and 5xx retried then `provider_unavailable`.*
- [ ] Add mocked integration tests and a manual verification checklist. *Mocked: `MusicSyncJobTests`, `MusicApiTests` (`FakeAppleMusic`). Manual check against the real API: not done (ADR 0007, "Not verified").*
- [ ] iOS: a MusicKit module for the native app (none in Expo SDK 57); the web app connects through MusicKit JS.

## Verification
- [x] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results. *See PROJECT_TODO Phase 10, 2026-11-05.*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations. *`packages/music-providers/README.md`, `services/api/src/features/music-sync/README.md`.*
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
