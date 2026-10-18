# Spotify sync worker

Design and configuration: [ADR 0005](../../docs/adr/0005-spotify-integration.md).

| Job kind | Handler | When |
| --- | --- | --- |
| `spotify.sync` | `SpotifySyncJob` | Queued by `request_spotify_sync()` on connect, manual sync, and schedule. |
| `spotify.schedule_syncs` | `ScheduleSpotifySyncsJob` | Hourly; queues connections not attempted within `Spotify__SyncEvery` (1 day). |

`SpotifySignals.Reduce` turns the Spotify library into `user_artist_signals` rows for tracked, active artists and
drops everything else before it is stored. A track counts for its primary artist only (provisional; co-credits are
an open decision in `PROJECT_TODO.md`).

Needs the same `Spotify__ClientId` and `Spotify__TokenKey` as the API.

## Known limits
- Scores are not recomputed after a sync yet (Phase 5 scoring worker).
- A sync is capped at 5,000 saved tracks, 50 playlists, and 500 tracks per playlist.
