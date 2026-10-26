# Album journal (app)

Screens for the Albums and Journal API (`services/api/src/features/album-journal`). Rules for showing Spotify
data: [ADR 0006](../../../../../docs/adr/0006-album-catalog.md).

| File | Route | What |
| --- | --- | --- |
| `ArtistAlbumsScreen.tsx` | `/artist/[artistId]/albums` | An artist's releases, newest first. Opened from an artist row on the home screen. |
| `AlbumScreen.tsx` | `/album/[albumId]` | Cover, details, tracks, your latest log (edit, log again), and other fans' reviews. |
| `LogForm.tsx` | | Rating (half-star steps), review, listen date, visibility. Creates with one `Idempotency-Key` per attempt, or edits. |
| `JournalScreen.tsx` | `/journal` | Your diary with delete and "show more". |
| `AlbumRow.tsx`, `SpotifyAttribution.tsx` | | Album list row (cover unaltered); "from Spotify" line with the link back. |
| `journal.ts` | | Types from the contract and small helpers (ratings, dates, points text). |
| `journalQueue.ts`, `pendingLogs.ts` | | Offline queue: entries kept on the device until the API accepts them; sync on sign-in, reconnect, foreground, Retry. |
| `queueStore.ts`, `queueStore.native.ts` | | Where the queue is kept: localStorage (web), a JSON file in the documents directory (iOS/Android). |
| `PendingLogCard.tsx`, `JournalSync.tsx` | | A waiting entry with Retry, Edit, Discard; the root-layout component that runs the sync. |

## Spotify rules applied here
- Cover art is shown as Spotify serves it: no crop, overlay, or filter; only rounded corners.
- Every screen with album data says it comes from Spotify and links to the album on Spotify.
- Delisted albums show no cover or link and can't be logged again.
- TODO: swap the "Spotify" wordmark text in `SpotifyAttribution` for the official logo asset.

## Offline logging
Saving a log or an edit tries the API first. If the call can't get through (no network, or a 429/5xx), the entry
is kept on the device with the `Idempotency-Key` of that first attempt and shown as "Saved on this device". The
queue sends entries oldest first whenever the app is signed in and something happens (sign-in, the browser's
`online` event, the app coming to the foreground, a new entry, Retry now), and after a failed attempt on a
backoff timer (5 s, 15 s, 1 min, 5 min). Because the key is reused, a response lost on the way can't log the
album twice. An entry the API refuses (validation, album gone, idempotency conflict) stays with its reason
until the fan edits, retries, or discards it. Entries belong to the account that made them; another account's
wait for it to sign in again. Not covered: browsing albums offline (the album page must already be open), and
deleting a log offline.

## Not yet
- Comments on logs and album lists (separate features).
- Offline browsing of albums and the journal (nothing is cached for reading).

## Tests
`*.test.tsx` next to each screen (Jest + React Native Testing Library): album detail and logging with points,
editing and relistens, delisted albums, the diary with delete, and the artist album list.
