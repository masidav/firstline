# TODO — Album journal mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build album search and album detail/logging screens for tracked artists. (Artist albums, album detail with logging; search is by artist on the home screen for now. Verified 2026-10-21: `npm run typecheck`, `npm test` (AlbumScreen 3, JournalScreen 2, ArtistAlbumsScreen 2).)
- [x] Support 1–10 ratings, reviews, listen dates, and relisten entries. (`LogForm`; relisten decided by the server.)
- [x] Allow selecting public, followers-only, or private visibility.
- [x] Provide edit and delete/soft-delete interactions according to API behavior. (Edit on the album screen, delete in the journal.)
- [ ] Persist offline journal changes locally and show pending-sync status.
- [ ] Reconcile server errors and conflicts without silently losing user entries.
- [x] Display cached album metadata and graceful missing-artwork states. (Cover art unaltered, "from Spotify" line and album link on every catalog screen, placeholder when there is no art: [ADR 0006](../../../../../docs/adr/0006-album-catalog.md). Official logo asset still to add.)
- [ ] Add component and offline-sync tests. (Component tests done; offline sync not built.)

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-21: `npm run typecheck`, `npm test` (AlbumScreen 3, JournalScreen 2, ArtistAlbumsScreen 2).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
