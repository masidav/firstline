# Spotify sync domain/API

Connect, inspect, disconnect, and manually sync Spotify (`openapi.yaml`, `/me/spotify`). Design and configuration:
[ADR 0005](../../../../../docs/adr/0005-spotify-integration.md).

| File | What |
| --- | --- |
| `SpotifyEndpoints.cs` | `GET/PUT/DELETE /me/spotify`, `POST /me/spotify/sync`. |
| `SpotifyConnections.cs` | `SpotifyStatus` (also returned in `GET /me`) and the Spotify problem codes. |

- Uses `FandomScore.Spotify` (`packages/spotify`) for the code exchange and token encryption.
- Syncs run in the worker (`workers/spotify-sync`); these endpoints only queue them with `request_spotify_sync()`.
- Needs `Spotify__ClientId`, `Spotify__RedirectUris`, `Spotify__TokenKey` (see `.env.example`). Without a client ID,
  connecting returns `503 spotify_unavailable`.
