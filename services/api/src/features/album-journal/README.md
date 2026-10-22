# Album journal API

openapi tags **Albums** and **Journal** (and `GET /users/{handle}/logs` from Social). Catalog decision and artwork
rules: [ADR 0006](../../../../../docs/adr/0006-album-catalog.md). Schema: `albums`, `tracks`, `album_logs` (0001),
catalog columns (0007), `idempotency_keys` and journal indexes (0009).

| File | What |
| --- | --- |
| `AlbumEndpoints.cs` | `GET /artists/{id}/albums` (live releases, newest first), `GET /albums/search` (local catalog by title or artist name; Spotify artist suggestions only when nothing matches), `GET /albums/{id}` (tracks, community rating, my latest log), `GET /albums/{id}/logs` (community logs visible to the caller). |
| `JournalEndpoints.cs` | `POST /logs`, `GET/PATCH/DELETE /logs/{id}`, `GET /me/logs`, `GET /users/{handle}/logs`. |
| `Journal.cs` | Response records, the shared album and log queries, visibility rule, paging, and award lookup. |
| `../../Infrastructure/Idempotency.cs` | `Idempotency-Key` replay store used by `POST /logs` (same key and body replays the stored 201; different body is `409 idempotency_conflict`; keys live 24 hours per user). |

## Rules
- **Relisten is decided here:** a log is a relisten when the same fan already has a live log of that album.
  A per-user advisory lock keeps two simultaneous first logs from both counting as first.
- **Points come from the database.** Triggers on `album_logs` award `album_logged` / `album_relisten` on insert and
  `review_written` for reviews of 100+ characters (also when a review is added later; shortening it reverses the
  award). The API only reports `points_awarded`: ledger rows created by that write.
- **Visibility:** own logs always; `public` to everyone; `private` is 404 to others. `followers` is visible to
  the owner only until follows exist (Phase 8). Editing someone else's visible log is 403.
- **Soft delete:** `deleted_at` is set, the triggers reverse the points, and the log leaves every listing.
- **Delisted albums** (Spotify no longer lists them) cannot be logged anew, are left out of lists and search, but
  `GET /albums/{id}` still returns them without cover art or Spotify id so old logs keep working.
- **Dates:** `listened_on` defaults to today (UTC) and must be between 1900-01-01 and tomorrow (UTC), so every
  local "today" is accepted. Release dates with year or month precision carry `release_date_precision`.
- **Paging:** opaque offset cursors (`PageCursor`), 25 per page by default, 100 at most.

## Tests
`tests/FandomScore.Tests/JournalApiTests.cs`: album lists and detail (including a delisted album), search and
Spotify suggestions, logging with points and relistens, validation, unknown and delisted albums, idempotency
replay and conflict, visibility and ownership, review edits awarding once, and soft delete reversing points.
`database/tests/invariants.sql` covers the `idempotency_keys` constraints.

## Not yet
- Offline-created entries: the app needs a local queue that sends each entry with its own `Idempotency-Key`.
- Followers-only visibility waits for the follow relationship (Phase 8); comments and lists are separate features.
