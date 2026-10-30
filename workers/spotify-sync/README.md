# Spotify sync worker

Design and configuration: [ADR 0005](../../docs/adr/0005-spotify-integration.md).

| Job kind | Handler | When |
| --- | --- | --- |
| `spotify.sync` | `SpotifySyncJob` | Queued by `request_spotify_sync()` on connect, manual sync, and schedule. |
| `spotify.schedule_syncs` | `ScheduleSpotifySyncsJob` | Hourly; queues connections not attempted within `Spotify__SyncEvery` (1 day), and a play poll for synced connections not polled within `Spotify__PollPlaysEvery` (1 hour). |
| `spotify.poll_plays` | `PollPlaysJob` | Extends the `user_plays` ledger from the last 50 plays after the stored cursor; prunes plays older than `Spotify__PlayHistoryRetention` (400 days); queues a rescore for artists with new plays. |

Before reducing, `SpotifySyncJob.TrackTopArtistsAsync` adds the fan's Spotify top artists that nobody tracks yet
(`track_artist()`, like `POST /artists`: the fan is `added_by`, the daily add limit applies, onboarding is queued).
At most `Spotify__AutoTrackTopArtists` (20) per sync, best rank first; off when 0 or without `Spotify__ClientSecret`.
Activation then queues the fan's next sync, which scores them. `spotify_connections.top_artists_added` records the count.

`SpotifySignals.Reduce` turns the Spotify library into `user_artist_signals` rows for tracked, active artists
(liked and playlist tracks, album depth, top-artist ranks over three time ranges, top tracks, playlists featuring
or dedicated to the artist, follows), `user_track_likes`, `user_album_saves`, and the plays that seed `user_plays`,
and drops everything else before it is stored. Follows need scope `user-follow-read`; a connection without it
is synced without follows. A track counts for its primary artist only (provisional; co-credits are
an open decision in `PROJECT_TODO.md`).

Saves are dated for fraud detection (`burst_engagement`, migrations 0045 and 0048): `user_track_likes.saved_at` is
Spotify's `added_at` of the like or of the earliest playlist add (a playlist item Spotify leaves undated is stamped
when first seen, by a trigger), and `user_artist_signals.followed_since` is set by a trigger when the follow first
appears and cleared when it ends. So `SaveAsync` updates, prunes, and adds rows rather than replacing them, and stays
idempotent: a re-sync with the same library changes nothing.

Needs the same `Spotify__ClientId` and `Spotify__TokenKey` as the API.

## Known limits
- A sync is capped at 5,000 saved tracks, 1,000 saved albums, 1,000 followed artists, 50 playlists, and 500 tracks
  per playlist; each sync is about 12 requests plus one per playlist and per 50 items.
- The play ledger only sees what the hourly poll catches: a fan who plays more than 50 tracks in an hour loses the
  overflow. Lower `Spotify__PollPlaysEvery` for heavier listeners (one token refresh and one request per poll).
