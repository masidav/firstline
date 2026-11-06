# Apple Music and Tidal API feature

Connect, inspect, disconnect, and manually sync Apple Music and Tidal (`openapi.yaml`, `/me/music`). Design and
configuration: [ADR 0007](../../../../../docs/adr/0007-music-providers.md).

| Endpoint | What |
| --- | --- |
| `GET /me/music` | One `MusicStatus` per provider. |
| `GET /me/music/apple_music/developer-token` | A MusicKit developer token for the app (short-lived). |
| `GET/PUT/DELETE /me/music/{provider}` | Status; connect (Apple Music: `music_user_token`; Tidal: `code`, `code_verifier`, `redirect_uri`); disconnect (deletes only that provider's synced rows). |
| `POST /me/music/{provider}/sync` | Manual sync, once per 15 minutes per provider. |

- `MusicEndpoints.cs` holds all of it, including the problem codes `music_not_connected`, `music_reauth_required`,
  `music_in_use`, `music_unavailable`.
- Syncs run in the worker (`workers/music-sync`); these endpoints only queue them with `request_music_sync()`.
- Needs the `MusicProviders` configuration and `Spotify__TokenKey` (see `.env.example`). An unconfigured provider
  answers `503 music_unavailable`.
- Tests: `tests/FandomScore.Tests/MusicApiTests.cs` (connect for both providers, one Tidal account per fan,
  disconnect scoped to the provider, validation, rate limit, unconfigured providers).

## Known limitations
- Apple Music has no account id, so the one-account-per-fan rule can't apply to it (ADR 0007).
- Neither provider has been tried for real yet; see the package README.
- Not on `Me` yet: the app reads `GET /me/music` separately.
