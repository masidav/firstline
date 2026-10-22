# TODO — Artist onboarding worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Accept only a valid artist record in onboarding status. (Skipped otherwise; `ArtistOnboardingJob`.)
- [x] Fetch artist albums and tracks from the chosen catalog provider. (Spotify, one market, primary artist only: [ADR 0006](../../docs/adr/0006-album-catalog.md); `CatalogLoader`.)
- [ ] Optionally resolve MusicBrainz identifiers and artist links if that decision is approved. *Not now (ADR 0006, 2026-10-21); the columns stay empty.*
- [x] Derive baseline inputs from permitted public proxy metrics and record their provenance. (Spotify no longer returns followers or popularity; `proxy_metrics` holds the catalog size, source, and time, ADR 0006.)
- [x] Generate and persist a synthetic baseline curve and blend parameter. (`ProvisionalBaseline`: lognormal from the `Baseline` settings, marked `provisional`; parameters are still an open decision.)
- [x] Verify that a baseline exists before activating the artist. (`trg_artist_activate`, now also requires the catalog; migration 0007.)
- [x] Mark the artist active and stamp activated_at only after required work succeeds.
- [x] Queue an immediate sync for the user who added the artist after activation. (`request_spotify_sync(added_by)` in the activation transaction.)
- [x] Implement retry, failure state/alerting, and safe reprocessing. (Queue retries with backoff; `failed` jobs keep `last_error`; `request_artist_onboarding()` re-queues; missing artist → `paused`. No alerting beyond logs yet: Phase 12 monitoring.)
- [x] Test duplicate jobs, missing catalog data, baseline failures, and activation constraints. (ArtistOnboardingJobTests, invariants.sql.)

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-21: `dotnet test` 109/109 (ArtistOnboardingJobTests 15, invariants).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
