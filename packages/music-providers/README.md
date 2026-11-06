# FandomScore.MusicProviders

Apple Music and Tidal clients, options, and the shared HTTP retry policy (ADR 0007). Shared by the API (connect:
`services/api/src/features/music-sync`) and the worker (sync: `workers/music-sync`).

| File | What |
| --- | --- |
| `MusicProviderOptions.cs` | `MusicProviders` section: Apple Music key, Tidal client, sync intervals and limits. `MusicProvider` names the providers and the token purpose. |
| `AppleMusicClient.cs` | `me/storefront` to check a user token, the library reads. |
| `AppleMusicDeveloperTokens.cs` | Developer tokens (ES256 JWT with the MusicKit key), one per process, renewed before expiry. A singleton because the token library caches signature providers per key and the typed client is transient. |
| `TidalClient.cs` | Token endpoint (PKCE exchange, refresh), `users/me`, the collection and playlist reads (JSON:API, cursor paging), artist lookups. |
| `MusicModels.cs` | `MusicLibrary`: a provider's library in one shape for the matcher. |
| `MusicErrors.cs` | Authorization (reconnect), request (don't retry), unavailable (retry later). |
| `ProviderHttp.cs` | Retries: short 429s and server errors a few times; longer waits surface as unavailable. |
| `MusicProviderSetup.cs` | `AddMusicProviders(configuration)`. |

Stored tokens are encrypted by the Spotify package's `SpotifyTokenProtector` with `Spotify__TokenKey` and a purpose
per provider (`MusicProvider.TokenPurpose`).

**Not yet tried against the real APIs.** Tests fake both (`tests/FandomScore.Tests/FakeAppleMusic.cs`,
`FakeTidal.cs`). Before the first real connection, run one Apple Music and one Tidal account through connect and
sync on the local stack and check the paths listed in ADR 0007 ("Not verified").
