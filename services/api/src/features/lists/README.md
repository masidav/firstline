# Album lists API

openapi tag **Lists** plus `GET /users/{handle}/lists`: `POST /lists`, `GET /me/lists`, `GET/PATCH/DELETE /lists/{listId}`,
`POST /lists/{listId}/items`, `PATCH/DELETE /lists/{listId}/items/{albumId}`. Code: `ListEndpoints.cs`.
Schema: `lists`, `list_items` (0001; `UNIQUE (list_id, position)` is deferred so a reorder commits as one change).

## Rules
- **Order:** positions are 1-based and contiguous. Adding without a position appends; adding at a position (or
  moving to one) shifts the items in between. Positions past the end are clamped. Removing closes the gap.
- **One album per list:** adding it again is `409 conflict`. Delisted or unknown albums are `404`.
- **Fields:** title 1 to 120 characters, description up to 2,000 (or null), note per item up to 500 (or null),
  visibility `public` / `followers` / `private`.
- **Visibility:** the owner sees everything; public lists are visible to all; private ones are 404 to others;
  followers-only equals private until follows exist (Phase 8). Only the owner changes a list (403 for others).
- Items carry the album reference with the Spotify id and cover (null once the album is delisted), so the app
  can show the Spotify link and artwork per ADR 0006.

## Tests
`tests/FandomScore.Tests/ListsApiTests.cs`: create, read, edit, delete; inserts, moves, and removals keeping
positions contiguous; duplicates; unknown and delisted albums; ownership and visibility; field validation.
