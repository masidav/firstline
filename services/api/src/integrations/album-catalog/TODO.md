# TODO — Album catalog integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Compare MusicBrainz and Spotify catalog options against current API and artwork/caching terms. Done 2026-10-21 (also Apple Music and Discogs): [ADR 0006](../../../../../docs/adr/0006-album-catalog.md).
- [x] Document the selected provider and attribution requirements before implementation. Spotify; rules and sources in ADR 0006.
- [x] Implement artist/album/track metadata lookup and normalized internal response types. (`SpotifyClient.GetArtistAlbumsAsync`, `GetAlbumTracksAsync`; `SpotifyAlbum`, `SpotifyAlbumTrack`. Verified 2026-10-21: ArtistOnboardingJobTests.)
- [x] Cache metadata with a documented TTL and refresh policy consistent with provider terms. (`catalog.refresh` every 7 days, delist removed releases; ADR 0006. Verified 2026-10-21.)
- [x] Handle missing artwork, unavailable releases, and provider rate limits. (Nullable `cover_url`; `delisted_at`; 429/5xx retried in the client then by the queue. App-side placeholder is the journal UI's job.)
- [ ] Ensure albums are exposed in the app only for tracked artists.
- [x] Document limitations around co-credited albums and collaborations. (Primary artist only; `workers/artist-onboarding/README.md`, ADR 0006.)
- [x] Add mocked provider tests and catalog-cache tests. (ArtistOnboardingJobTests with `FakeSpotify`.)

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
