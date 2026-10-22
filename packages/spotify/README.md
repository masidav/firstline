# Spotify integration (`FandomScore.Spotify`)

Class library shared by the API and the worker. Design and configuration: [ADR 0005](../../docs/adr/0005-spotify-integration.md).

| File | What |
| --- | --- |
| `SpotifyClient.cs` | Token endpoint (PKCE code exchange, refresh, client credentials), the Web API reads a sync needs, artist search and lookup, and the album catalog reads (`GetArtistAlbumsAsync`, `GetAlbumTracksAsync`, ADR 0006), with paging, retries, and rate-limit handling. |
| `SpotifyTokenProtector.cs` | AES-256-GCM encryption of stored refresh tokens, with key rotation. |
| `SpotifyOptions.cs` | Configuration section `Spotify` and the required scopes. |
| `SpotifyErrors.cs` | `SpotifyAuthorizationException` (reconnect needed), `SpotifyRequestException` (refused, e.g. 403), `SpotifyUnavailableException` (retry later). |
| `SpotifySetup.cs` | `services.AddSpotify(configuration)`. |

Used by `services/api/src/features/spotify-sync` (connect), `services/api/src/features/artists` (search and add),
`workers/spotify-sync` (sync), and `workers/artist-onboarding` (catalog).
Tests: `tests/FandomScore.Tests/Spotify*Tests.cs`, with Spotify replaced by `FakeSpotify`.

## Known limits
- Only endpoints available to development-mode apps since February 2026 are used.
- Development mode allows 5 allowlisted Spotify users; others get 403 (`spotify_forbidden`).
- No live Spotify integration test yet; see `TODO.md`.
