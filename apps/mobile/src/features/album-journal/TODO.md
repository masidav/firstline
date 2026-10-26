# TODO — Album journal mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build album search and album detail/logging screens for tracked artists. (Artist albums, album detail with logging; search is by artist on the home screen for now. Verified 2026-10-21: `npm run typecheck`, `npm test` (AlbumScreen 3, JournalScreen 2, ArtistAlbumsScreen 2).)
- [x] Support 1–10 ratings, reviews, listen dates, and relisten entries. (`LogForm`; relisten decided by the server.)
- [x] Allow selecting public, followers-only, or private visibility.
- [x] Provide edit and delete/soft-delete interactions according to API behavior. (Edit on the album screen, delete in the journal.)
- [x] Persist offline journal changes locally and show pending-sync status. *Done 2026-10-26: a log or edit made
      without a connection (or when the API is down) is kept on the device (`journalQueue`, a JSON file on iOS and
      Android, localStorage on the web) with its Idempotency-Key, shown as "Saved on this device" on the album,
      in the journal, and as a banner on home, and sent oldest first on sign-in, reconnect, foreground, or Retry,
      with backoff (5 s, 15 s, 1 min, 5 min) after a failed attempt.*
- [x] Reconcile server errors and conflicts without silently losing user entries. *An entry the API refuses
      (validation, deleted album, idempotency conflict) stays with the reason until the fan edits, retries, or
      discards it; a second edit of the same server log replaces the first; another account's entries wait for it.*
- [x] Display cached album metadata and graceful missing-artwork states. (Cover art unaltered, "from Spotify" line and album link on every catalog screen, placeholder when there is no art: [ADR 0006](../../../../../docs/adr/0006-album-catalog.md). Official logo asset still to add.)
- [x] Add component and offline-sync tests. *journalQueue 9 (send, retry with the same key, refusal, validation then
      edit, per-account, PATCH replace, ended session, stored entries, server error), AlbumScreen 2 (offline then
      back online with points; edit and discard), JournalScreen 1 (refused entry, Retry now), HomeScreen 1 (banner).
      Browser check 2026-10-26: Chrome offline via Playwright, logged an album, back online, synced with points.*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-21: `npm run typecheck`, `npm test` (AlbumScreen 3, JournalScreen 2, ArtistAlbumsScreen 2).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone. *2026-10-26, offline sync.*
