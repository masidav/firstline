# Album lists (app)

| File | Route | What |
| --- | --- | --- |
| `ListsScreen.tsx` | `/lists` | Your lists (`GET /me/lists`) and a form to create one (`POST /lists`). Reached from the journal's "Your lists". |
| `ListScreen.tsx` | `/list/[listId]` | One list in order. Owners move items up and down (`PATCH .../items/{albumId}` with the new position), remove them, and delete the list; everyone else reads. |
| `AddToList.tsx` | | "Add to list" on the album screen: picks one of your lists and appends the album; a 409 means it was already there. |
| `lists.ts` | | Types from the contract and the "3 albums · private" line. |

Covers follow the Spotify rules (unaltered art, "from Spotify" line) through the shared `AlbumRow` and
`SpotifyAttribution` from the journal feature.

Not yet: editing a list's title or visibility after creation, notes on items, drag-to-reorder.

Tests: `ListsScreen.test.tsx`, `ListScreen.test.tsx`.
