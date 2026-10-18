# TODO — Artist onboarding worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Accept only a valid artist record in onboarding status.
- [ ] Fetch artist albums and tracks from the chosen catalog provider.
- [ ] Optionally resolve MusicBrainz identifiers and artist links if that decision is approved.
- [ ] Derive baseline inputs from permitted public proxy metrics and record their provenance.
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
