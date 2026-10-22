# Album journal (app)

Screens for the Albums and Journal API (`services/api/src/features/album-journal`). Rules for showing Spotify
data: [ADR 0006](../../../../../docs/adr/0006-album-catalog.md).

| File | Route | What |
| --- | --- | --- |
| `ArtistAlbumsScreen.tsx` | `/artist/[artistId]/albums` | An artist's releases, newest first. Opened from an artist row on the home screen. |
| `AlbumScreen.tsx` | `/album/[albumId]` | Cover, details, tracks, your latest log (edit, log again), and other fans' reviews. |
| `LogForm.tsx` | | Rating (half-star steps), review, listen date, visibility. Creates with one `Idempotency-Key` per attempt, or edits. |
| `JournalScreen.tsx` | `/journal` | Your diary with delete and "show more"; offline logs waiting to send at the top; link to your lists. |
| `LogScreen.tsx` | `/log/[logId]` | One log with its comment thread (`src/features/comments`). |
| `outbox.ts` | | Logs saved without a connection, each with its `Idempotency-Key`; sent when the journal opens. Browser storage on web, memory elsewhere for now. |
| `AlbumRow.tsx`, `SpotifyAttribution.tsx` | | Album list row (cover unaltered); "from Spotify" line with the link back. |
| `journal.ts` | | Types from the contract and small helpers (ratings, dates, points text). |

## Spotify rules applied here
- Cover art is shown as Spotify serves it: no crop, overlay, or filter; only rounded corners.
- Every screen with album data says it comes from Spotify and links to the album on Spotify.
- Delisted albums show no cover or link and can't be logged again.
- TODO: swap the "Spotify" wordmark text in `SpotifyAttribution` for the official logo asset.

## Offline
A new log that can't reach the server goes to the outbox instead of failing. The album screen says it was saved
on the device; the journal lists it as "Waiting to send" and sends it when opened (or on "Send now"). The
request keeps the same `Idempotency-Key`, so the server can never record it twice. If the API rejects it
(for example a bad date), it stays listed as "Not saved: reason" until the fan discards it. The native app
needs a persistent store plugged into `defaultStorage()`; on web this is `localStorage`.

## Tests
`*.test.tsx` next to each screen (Jest + React Native Testing Library): album detail and logging with points,
editing and relistens, delisted albums, the offline path, the outbox, the diary with delete, and the artist
album list.
