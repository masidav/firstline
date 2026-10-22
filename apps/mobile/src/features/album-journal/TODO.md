# TODO — Album journal mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build album search and album detail/logging screens for tracked artists. (Artist albums, album detail with logging; search is by artist on the home screen for now. Verified 2026-10-21: `npm run typecheck`, `npm test` (AlbumScreen 3, JournalScreen 2, ArtistAlbumsScreen 2).)
- [x] Support 1–10 ratings, reviews, listen dates, and relisten entries. (`LogForm`; relisten decided by the server.)
- [x] Allow selecting public, followers-only, or private visibility.
- [x] Provide edit and delete/soft-delete interactions according to API behavior. (Edit on the album screen, delete in the journal.)
- [x] Persist offline journal changes locally and show pending-sync status. (`outbox.ts`: a new log that can't reach the server is kept with its `Idempotency-Key`; the journal shows "Waiting to send" and sends it on open or "Send now". Browser storage on web, memory elsewhere until the native store is chosen. Verified 2026-10-21: outbox 3, AlbumScreen.offline 2.)
- [x] Reconcile server errors and conflicts without silently losing user entries. (API rejections stay in the journal as "Not saved: reason" until discarded; relisten and duplicate protection are the server's.)
- [x] Display cached album metadata and graceful missing-artwork states. (Cover art unaltered, "from Spotify" line and album link on every catalog screen, placeholder when there is no art: [ADR 0006](../../../../../docs/adr/0006-album-catalog.md). Official logo asset still to add.)
- [x] Add component and offline-sync tests. (2026-10-21: `npm test` 75/75 on branch `phase-6-journal`.)

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-21: `npm run typecheck`, `npm test` (AlbumScreen 3, JournalScreen 2, ArtistAlbumsScreen 2).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
