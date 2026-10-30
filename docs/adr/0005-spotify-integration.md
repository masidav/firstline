# ADR 0005: Spotify connection and sync

- **Status:** Accepted (2026-10-18). Implemented in Phase 4 (backend). Amended 2026-10-20: app token
  (client credentials) for artist search and lookup. Amended 2026-10-26: playback scopes and the player endpoints
  for the app's now-playing bar and play buttons; access tokens held in API memory for them.
- **Code:** `packages/spotify` (`FandomScore.Spotify`: client, token encryption, options),
  `services/api/src/features/spotify-sync` (endpoints), `services/api/src/features/artists` (search and add),
  `workers/spotify-sync` (jobs), `database/migrations/0005_spotify_sync.sql`.
- **Contract:** `openapi.yaml`, `/me/spotify`, `/me/spotify/sync`, `/me/spotify/player`, `/artists/search`, and `POST /artists`.
- **Related:** [ADR 0006](0006-album-catalog.md) uses the same app token for the album catalog.

## Decision

| Concern | Choice |
| --- | --- |
| Authorization | The app runs Spotify's **authorization code flow with PKCE** and sends `code`, `code_verifier`, and `redirect_uri` to `PUT /me/spotify`. The server exchanges the code (client ID only, no client secret) and checks every required scope was granted. `redirect_uri` must be in `Spotify__RedirectUris`. |
| Artist search and lookup | `GET /artists/search` and `POST /artists` call Spotify with an **app token** from the **client credentials flow** (`Spotify__ClientId` + `Spotify__ClientSecret`, HTTP Basic). Users don't need to connect Spotify to find or add artists. The token is held in memory per process and fetched again a minute before it expires (or once after a 401). Without a client secret both endpoints return `503 spotify_unavailable`; user connections keep working. |
| Scopes | `user-library-read`, `user-top-read`, `user-read-recently-played`, `playlist-read-private`, `playlist-read-collaborative`, `user-follow-read` (added 2026-10-24: connections made without it skip follows, and the app asks the fan to connect again), `user-read-playback-state`, `user-modify-playback-state` (added 2026-10-26 for the now-playing bar and play buttons; older connections get `reconnect_needed` from the player). The playback scope is the only one that acts on the fan's Spotify, and only when the fan presses a button. Nothing writes to the fan's library, playlists, or profile. |
| Token storage | Only the refresh token is kept, in `spotify_connections.refresh_token_enc`: **AES-256-GCM**, key from `Spotify__TokenKey`, user id authenticated with it (a token copied to another row will not decrypt). Each value names its key, so keys rotate via `Spotify__PreviousTokenKeys`; every sync re-encrypts with the current key. Access tokens are never stored in the database or logged. Since 2026-10-26 the API keeps each fan's access token in process memory for the player, until a minute before it expires. |
| Player | `/me/spotify/player` (2026-10-26) reads what the fan's Spotify is playing and sends play, pause, and skip to the fan's Spotify app through Spotify Connect; the app plays no audio itself. The API refreshes the token under the same per-fan advisory lock as the sync (Spotify may rotate the refresh token) and waits at most `Spotify__PlayerLockWait` (5 s) for a running sync, then answers `503`. Playback is never stored, may be an untracked artist, and is shown only to the fan. Controlling playback needs Spotify Premium; otherwise, and when Spotify isn't open on a phone or computer, the app opens the song in Spotify. Details: `services/api/src/features/spotify-player/README.md`. |
| One account each | A Spotify account can be connected to one user (`409 spotify_in_use`), so one listening history can't qualify several accounts. Switching Spotify accounts deletes data synced from the old one. |
| Sync | Job `spotify.sync` (worker), queued by `request_spotify_sync()` in the same transaction as the change that needs it. Refreshes the token (saving a rotated one at once), reads saved tracks and albums, top artists and top tracks over all three time ranges, followed artists, the last 50 plays, and tracks in playlists the user owns or collaborates on (with each item's `added_at`), then brings the user's `user_artist_signals` and `user_track_likes` up to date (rows updated, pruned, and added, so `followed_since` and a playlist row's first-seen date survive; migration 0048; an undated item already there when the sync first sees its artist is marked baseline and never counts toward a burst, migration 0049), replaces `user_album_saves`, all in one transaction, and seeds the `user_plays` ledger. Repeating a sync gives the same result. |
| Play ledger | Spotify returns only the last 50 plays, so job `spotify.poll_plays` runs hourly per connection (`Spotify__PollPlaysEvery`), reads plays after the stored cursor, keeps those of tracked active artists in `user_plays`, and prunes rows older than `Spotify__PlayHistoryRetention` (400 days). It shares the sync's per-user lock because both refresh the token. Signals: plays in the last 90 days and weekly streaks (migration 0015, 2026-10-24). |
| Tracked artists only | Spotify returns everything; the worker drops untracked (and not yet active) artists **in memory** before writing. `user_track_likes` rows exist only for tracks in our catalog. Nothing about other artists is stored or logged. |
| Top artists are tracked for the fan | Before dropping, the sync adds the fan's Spotify top artists that nobody tracks yet through `track_artist()`, exactly like `POST /artists` (the fan is `added_by`, the daily add limit applies, onboarding is queued). Up to `Spotify__AutoTrackTopArtists` (20) per sync, best rank first; off without the client secret. The count is `SpotifyStatus.top_artists_added`, and the home screen lists them while they onboard. Decided 2026-10-22 so a new fan's page is lively right after connecting. "On Repeat" and other Spotify-owned playlists cannot be read by apps in development mode since Spotify's November 2024 API changes, so top artists are the source. |
| Credit | A track counts for its **primary (first-credited) artist** only, as `albums.artist_id` does. Provisional: co-credits are an open product decision (`PROJECT_TODO.md`); the rule lives in `SpotifySignals.Reduce`. |
| Schedule | `spotify.schedule_syncs` runs hourly and queues connections whose last attempt is over `Spotify__SyncEvery` (default 1 day) old. `reauth_required` connections are skipped until the user reconnects. |
| Manual sync | `POST /me/spotify/sync`: one per 15 minutes per user (stored `sync_requested_at`, so it holds across API replicas). A queued or running sync is returned instead of queuing another. |
| Concurrency | A per-user advisory lock stops two syncs of one user running together (they would race on a rotating refresh token). The final write locks the connection row; disconnecting deletes that row first, so a sync finishing during a disconnect writes nothing. |
| Errors | `invalid_grant` or 401 → `reauth_required` (not retried). 403 (in development mode: account not allowlisted) → `failed` / `spotify_forbidden`. 429 with a short `Retry-After` (≤ 10 s) and 5xx on reads are retried in the job; longer limits and outages go back to the queue with backoff, then `failed` / `spotify_unavailable`. Token calls are not retried on 5xx (a code works once). |
| Limits per sync | 5,000 saved tracks, 50 playlists, 500 tracks per playlist (`Spotify__Max*`). Keeps a sync to a few hundred requests, inside the job lease. |

## Why
- **PKCE with the exchange on the server:** the mobile app never needs a client secret, and the refresh token
  never stays on the device.
- **Client credentials for the catalog (decided 2026-10-20):** adding an artist shouldn't require a Spotify
  connection, and an app token isn't tied to any user. The cost is one more server secret (`Spotify__ClientSecret`,
  in Secret Manager in production). It stays on the server; the app never sees it. Rejected alternative: search
  with the signed-in user's own Spotify token, which would make search unavailable until the user connects.
- **App-side AES-GCM instead of Cloud KMS for now:** no GCP dependency in local development or tests, and the
  key is one secret in Secret Manager like `Auth__SigningKey`. The format carries a key id, so moving to KMS
  envelope encryption later only changes how the key is obtained.
- **Snapshot replacement instead of incremental sync:** Spotify's library endpoints have no change feed, and
  replacing the user's rows is simple and idempotent. `user_artist_signals` is state, not ledger events.

## Spotify platform constraints (checked 2026-10-18)
- **Development mode** (every new app): up to **5 allowlisted users**, the app owner needs **Spotify Premium**,
  and quotas are per developer account (July 2026 changelog). Users not on the allowlist get 403.
- **Extended quota** (needed for launch): only for organizations, and needs **250k monthly active users**.
  This is a launch blocker to resolve with Spotify; see the open question in `PROJECT_TODO.md`.
- Since February 2026, development-mode apps lose `GET /users/{id}/playlists`, batch lookups, artist top tracks,
  and fields such as track and artist `popularity` and `/me` email. Playlist contents come from
  `GET /playlists/{id}/items` (the track is in `item`), and only for playlists the user owns or collaborates on.
  The client uses only endpoints still listed as available.
- Also since February 2026: search returns at most **10 results** per request (default 5), and artist objects
  no longer include `followers` or `popularity`. `GET /artists/{id}` (single) still works.
- **Risk:** a Spotify statement quoted by client-library maintainers (Feb 2026) says Spotify is "moving away
  from the Client Credentials flow for metadata endpoints", and app-token calls fail when the app owner's
  Premium lapses. If app tokens stop working for search, fall back to the user's token (rejected alternative
  above) or another catalog source. Checked 2026-10-20 against Spotify's February 2026 migration guide.

## Consequences and known limits
- Scores don't recompute after a sync yet; that comes with the scoring worker (Phase 5).
- Recent plays are the last 50 only (Spotify's limit), so `recent_plays` is a short snapshot.
- Spotify has no token revocation API. Disconnecting deletes our token and data; the user can also remove the
  app in their Spotify account settings.
- Losing `Spotify__TokenKey` (without keeping it in `PreviousTokenKeys`) puts every connection in
  `reauth_required`.

## Configuration
| Variable | Meaning |
| --- | --- |
| `Spotify__ClientId` | Client ID from the Spotify developer dashboard. Not a secret. Empty disables Spotify (`503 spotify_unavailable` on connect). |
| `Spotify__ClientSecret` | Secret. Client secret from the Spotify dashboard. Only for app tokens (artist search and add); empty turns those off. Never logged. |
| `Spotify__RedirectUris` | Comma-separated redirect URIs the app may send. Each must also be registered in the Spotify dashboard. |
| `Spotify__TokenKey` | Secret. Base64 of 32 random bytes (`openssl rand -base64 32`). Required once `ClientId` is set; the API and worker refuse to start without it. Both processes need the same key. |
| `Spotify__PreviousTokenKeys` | Optional, comma-separated. Old keys still accepted for decryption during rotation. |
| `Spotify__PlayerLockWait` | Optional. How long a player request waits for a running sync of the same fan (default 5 s). |
| `Spotify__SyncEvery`, `Spotify__ManualSyncInterval`, `Spotify__MaxSavedTracks`, `Spotify__MaxPlaylists`, `Spotify__MaxPlaylistItems` | Optional overrides of the defaults above. |
