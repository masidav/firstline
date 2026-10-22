# TODO — Artist onboarding worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Accept only a valid artist record in onboarding status.
- [ ] Fetch artist albums and tracks from the chosen catalog provider. (Spotify, one market, primary artist only: [ADR 0006](../../docs/adr/0006-album-catalog.md).)
- [ ] Optionally resolve MusicBrainz identifiers and artist links if that decision is approved. *Not now (ADR 0006, 2026-10-21); the columns stay empty.*
- [ ] Derive baseline inputs from permitted public proxy metrics and record their provenance. (Spotify no longer returns followers or popularity; catalog size and our own data only, ADR 0006.)
- [ ] Generate and persist a synthetic baseline curve and blend parameter.
- [ ] Verify that a baseline exists before activating the artist.
- [ ] Mark the artist active and stamp activated_at only after required work succeeds.
- [ ] Queue an immediate sync for the user who added the artist after activation.
- [ ] Implement retry, failure state/alerting, and safe reprocessing.
- [ ] Test duplicate jobs, missing catalog data, baseline failures, and activation constraints.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
