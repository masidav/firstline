# Spotify player

The fan's own Spotify playback for the app's now-playing bar, and play, pause, and skip for its buttons
(requested 2026-10-26; `openapi.yaml`, `/me/spotify/player`). Design: [ADR 0005](../../../../../docs/adr/0005-spotify-integration.md),
amended 2026-10-26.

| File | What |
| --- | --- |
| `SpotifyPlayerEndpoints.cs` | `GET /me/spotify/player`, `PUT .../play`, `PUT .../pause`, `POST .../next`, `POST .../previous`. |
| `SpotifyPlayerAccess.cs` | The fan's access token: refreshed under the sync worker's per-fan lock, kept in memory only. |

## How it works
- **Spotify Connect, not in-app audio.** Commands go to Spotify's Web API, so the song plays in the fan's Spotify app
  on their phone or computer. The app itself plays no audio. Spotify's Web Playback SDK would play in the browser but
  works on the web only and needs the access token in the app; it was not used.
- **No active device:** a play is sent to a phone, computer, or tablet Spotify lists for the fan, active one first.
  Never to a speaker or TV, which could be in another room. With none, `409 spotify_no_device` and the app opens the
  song in Spotify instead.
- **Premium:** Spotify only lets apps control playback for Premium accounts (`409 spotify_premium_required`). Reading
  what's playing works for everyone. The app opens the song in Spotify instead.
- **Tokens:** the stored refresh token is used under the advisory lock `spotify.sync:{user}`, the same one the sync
  and play-poll jobs hold, because Spotify may rotate it. A rotated token is saved before anything else. The access
  token stays in this process's memory until a minute before it expires. While a sync holds the lock, a request waits
  up to `Spotify__PlayerLockWait` (5 s) and then answers `503 spotify_unavailable` with `Retry-After: 5`.
- **Nothing is stored.** Playback is read on request, cached 3 seconds per fan so several tabs share one Spotify call,
  and shown only to the fan. It may be an untracked artist; that never reaches the database or the logs. Using
  "listening now" as a fandom signal is a separate open decision (`PROJECT_TODO.md`).
- **Scopes:** `user-read-playback-state` and `user-modify-playback-state`, required since 2026-10-26. Connections made
  before lack them: `GET` answers `reconnect_needed`, commands `409 spotify_reauth_required`, and the Spotify screen
  asks the fan to connect again.

Tests: `tests/FandomScore.Tests/SpotifyPlayerApiTests.cs` (fake Spotify player routes in `FakeSpotify.cs`).
