# TODO — Tracked artists and catalog

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Implement artist search through the selected Spotify integration and match results by Spotify artist ID.
- [ ] Show tracked status without persisting arbitrary untracked search results.
- [ ] Implement the shared tracked-artist add operation using server-fetched provider data, never client-supplied artist identity fields.
- [ ] Use track_artist() or an equivalent transactional operation to enforce uniqueness and the UTC daily add limit.
- [ ] Handle concurrent attempts to add the same artist without creating duplicates.
- [ ] Insert new artists with onboarding status and expose status to the client.
- [ ] Queue catalog loading, baseline generation, and activation as background work.
- [ ] Activate only after the catalog and required baseline exist; record activation timestamp.
- [ ] Sync the adding user after activation and make other users eligible on their next scheduled sync.
- [ ] Implement paused status behavior for takedowns or removals.
- [ ] Add tests for duplicates, daily limits, onboarding failure, activation guard, and paused artists.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
