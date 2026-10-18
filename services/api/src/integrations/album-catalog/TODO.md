# TODO — Album catalog integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Compare MusicBrainz and Spotify catalog options against current API and artwork/caching terms.
- [ ] Document the selected provider and attribution requirements before implementation.
- [ ] Implement artist/album/track metadata lookup and normalized internal response types.
- [ ] Cache metadata with a documented TTL and refresh policy consistent with provider terms.
- [ ] Handle missing artwork, unavailable releases, and provider rate limits.
- [ ] Ensure albums are exposed in the app only for tracked artists.
- [ ] Document limitations around co-credited albums and collaborations.
- [ ] Add mocked provider tests and catalog-cache tests.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
