# TODO — Album journal API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Implement album search and metadata lookup for tracked artists only. (`GET /albums/search`, `GET /albums/{id}`, `GET /artists/{id}/albums`; Verified 2026-10-21: `dotnet test` (JournalApiTests 11, invariants).)
- [x] Use the selected catalog provider and comply with its artwork, attribution, and caching terms. (Spotify; the contract now carries Spotify ids and album type for the logo and link, and delisted albums lose cover art: [ADR 0006](../../../../../docs/adr/0006-album-catalog.md). The app side is in the journal UI TODO.)
- [x] Implement create, read, update, and delete/soft-delete behavior for album logs as defined by the product. (`/logs`; Verified 2026-10-21: `dotnet test` (JournalApiTests 11, invariants).)
- [x] Validate ratings as integer values from 1 to 10, representing half-star increments.
- [x] Support optional review text up to the specified limit and validate listen dates. (10,000 characters; 1900-01-01 to tomorrow UTC.)
- [x] Support relisten flags and multiple diary entries for the same album. (Relisten decided by the server.)
- [x] Enforce public, followers-only, and private visibility in every relevant query. (`Journal.VisibleToViewer`; followers-only equals private until follows exist, Phase 8.)
- [x] Prompt for tracked-artist onboarding when an album belongs to an untracked artist. (`untracked_artists` in `GET /albums/search` when nothing matches locally.)
- [x] Connect eligible log, relisten, and substantive-review events to the points ledger through idempotent award logic. (Triggers from 0001; `points_awarded` reported; `Idempotency-Key` on `POST /logs`.)
- [ ] Design conflict-safe synchronization for offline-created entries.
- [ ] Add tests for validation, ownership, visibility, rewards, duplicate requests, and offline reconciliation. (All but offline reconciliation: JournalApiTests.)

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-21: `dotnet test` (JournalApiTests 11, invariants).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
