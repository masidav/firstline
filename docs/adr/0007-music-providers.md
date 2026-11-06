# ADR 0007: Apple Music and Tidal as listening sources

- **Status:** Accepted (2026-11-05). Backend implemented (migration 0058); neither client has been run against the
  real API yet (see "Not verified" below). Requested 2026-11-05: "start adding in Apple Music and Tidal".
- **Code:** `packages/music-providers` (`FandomScore.MusicProviders`: options, Apple Music and Tidal clients),
  `services/api/src/features/music-sync` (endpoints), `workers/music-sync` (matcher, sync, schedule),
  `database/migrations/0058_music_providers.sql`.
- **Contract:** `openapi.yaml`, `/me/music`, `/me/music/{provider}`, `/me/music/{provider}/sync`,
  `/me/music/apple_music/developer-token`.
- **Related:** [ADR 0005](0005-spotify-integration.md) (Spotify, whose shape this follows), [ADR 0006](0006-album-catalog.md).

## Decision

| Concern | Choice |
| --- | --- |
| Why | A fan's listening lives where they listen. Spotify alone leaves Apple Music and Tidal fans with no score, and gives a gamer only one service to fake. A second source for the same fan is also a check on the first (see `docs/product/fandom-verification-signals.md`). |
| Apple Music authorization | **MusicKit**: the app gets a **Music User Token** (MusicKit JS `music.authorize()` on the web; MusicKit on iOS, not built yet) using a **developer token** the API signs with the MusicKit key (`GET /me/music/apple_music/developer-token`, ES256, `MusicProviders__AppleMusic__TeamId/KeyId/PrivateKey`). The app sends the user token to `PUT /me/music/apple_music`; the API checks it with one call (`me/storefront`) and stores it encrypted. It is long-lived and has no refresh; when Apple stops accepting it the connection goes `reauth_required`. |
| Tidal authorization | **Authorization code with PKCE** at `https://login.tidal.com/authorize`, scopes `user.read collection.read playlists.read`; the app sends `code`, `code_verifier`, `redirect_uri` to `PUT /me/music/tidal`; the API exchanges at `https://auth.tidal.com/v1/oauth2/token` (client id only), reads `users/me`, and stores the refresh token encrypted. Same shape as Spotify. |
| Token storage | `music_connections.token_enc`, AES-256-GCM through the Spotify package's `SpotifyTokenProtector` with **`Spotify__TokenKey`** (one stored-token key for every provider) and a **purpose per provider** (`apple_music-token`, `tidal-token`) authenticated with the user id, so a token stored for one provider or fan never decrypts as another's. Rotation through `Spotify__PreviousTokenKeys` works the same way. |
| One account each | A Tidal account connects to one fan account (`409 music_in_use`, unique `(provider, provider_user_id)`), and its hashed id joins `spotify_account_links` so the `spotify_account_reuse` rule sees a Tidal account passed through several fan accounts. **Apple Music has no user id in its API**, so one Apple Music library can be connected to several fan accounts. Known gap; the shared-device and address rules are the only check on it. |
| Matching to the catalog | The catalog is keyed by Spotify ids (ADR 0006) and Spotify's endpoints available to this app return no ISRC or UPC. So a provider's item is matched **by artist name** (normalized: lowercase, no accents, no "(feat. …)" or "- Remaster" tails, letters and digits only; a name two tracked artists share is skipped as ambiguous), then **by ISRC** when both sides have one, else **by normalized title** (the album title breaks a tie between editions); albums by UPC, else title. A code the provider gives for an item matched by title is **learned** into `tracks.isrc` / `albums.upc`, so later matches from any provider go by code. `workers/music-sync/MusicMatcher.cs`. |
| Where the rows go | `user_artist_signals`, `user_track_likes`, `user_album_saves`, `user_plays` have a **`provider` column in their primary key** (0058; `'spotify'` for every row that existed). Each sync replaces only its provider's rows; disconnecting a provider deletes only its rows. |
| How they score | `v_raw_scores` takes a fan's **strongest signal per artist across providers**: the greatest of each count, the best rank, a follow on any service; saves and plays union across providers (distinct tracks and albums). A fan on one service scores exactly as before; a fan on two is never counted twice for the same track. No new scoring rules: Apple Music's heavy rotation feeds `spotify_top_artist_short`, Tidal's favourite artist feeds `spotify_follow`, and so on. The rule keys keep their `spotify_` names for now. |
| What each provider gives | **Apple Music:** library songs (likes), library albums with `dateAdded`, library playlists and their songs, recently played (order only, no times), heavy rotation (artist rank). No follows, no play times, no user id. **Tidal:** collection tracks, albums (with `barcodeId`), favourite artists (follows), playlists and items, each with `addedAt`. No listening history at all. So neither feeds the play ledger (`user_plays`), play history, streaks, or the play-pattern fraud rules. |
| Sync | Job `music.sync` (payload `user_id`, `provider`), queued by `request_music_sync()`; `music.schedule_syncs` hourly queues connections older than `MusicProviders__SyncEvery` (1 day) for configured providers; manual sync once per 15 minutes per provider. Per-connection advisory lock. Errors as for Spotify: 401/403 (Apple) or `invalid_grant` (Tidal) → `reauth_required`; 5xx and long rate limits → back to the queue, then `failed` / `provider_unavailable`. |
| Tracked artists only | Everything about other artists is dropped in memory by the matcher before anything is stored (CLAUDE.md). |

## Why
- **Provider column instead of parallel tables:** one place for every listening signal, one view, one deletion
  path; the Spotify sync changed in five lines (its deletes and conflict targets now name `provider`).
- **Strongest-across-providers instead of a sum:** a fan who likes the same 40 songs on two services is one fan
  with 40 likes, not 80; and the existing tuning of saturations stays meaningful.
- **Name-and-title matching instead of a universal id:** the only thing available without an ISRC on our side.
  It's wrong for cover versions with the same title by a same-named artist, and for artists whose name two tracked
  artists share (skipped). Learned codes make it converge: once one provider has told us a track's ISRC, every
  provider matches it by code.
- **One token key for every provider:** one secret to manage and rotate; the purpose in the associated data keeps
  tokens apart.

## Not verified (do before the first fan connects)
- Neither client has been run against the real API. The tests fake both. `tools/dev/music_connect.py` (2026-11-06)
  runs a real account through connect and sync on the local stack (`tidal connect`, `apple-music connect`) and
  prints what was stored; that is the check to run once per provider. Tidal moved its collection endpoints in
  autumn 2026 (`/userCollection{Tracks,Albums,Artists,Playlists}/me/relationships/items`, cursor paging through
  `links.next`, artists through `/tracks?filter[id]=…&include=artists`); the client follows that, and the
  constants are at the top of `TidalClient.cs` if they move again. Apple Music's library endpoints
  (`me/library/songs?include=catalog` for the ISRC, `me/library/albums` for `dateAdded`, `me/recent/played/tracks`,
  `me/history/heavy-rotation`) are as documented; `canEdit` on playlists is read when present.
- Apple requires a MusicKit identifier and key (Apple Developer account, "Media Services"); the web app needs
  MusicKit JS and its developer token; the iOS app needs a MusicKit module (none in Expo SDK 57; not built).
- Tidal: register the app in the developer dashboard with the redirect URIs; confirm the scopes and that collection
  tracks are readable (a 2025 report said only albums, artists, and playlists were).
- Terms: both providers' developer terms on using listening data to decide eligibility (listed in the Apple Music
  integration TODO since Phase 10).

## Consequences and known limits
- No plays from either provider: a fan only on Apple Music or Tidal has no play history, streak, or 24/7 detection.
  The trust part `steady_listening` reads plays, so it stays 0 for them (open decision: count library growth instead).
- Fraud rules over saves (`burst_engagement`) read rows of every provider; a fan who likes the same 25 songs on two
  services looks like 50 saves in one window. Acceptable for review-only flags; refine if it shows up.
- Stats (`GET /me/stats`, listening) and the data export show provider rows; the listening stats sum across
  providers today (a known over-count for two-service fans, listed in the stats README).
- Apple Music can't be "one account per fan" (no user id).

## Configuration
| Variable | Meaning |
| --- | --- |
| `MusicProviders__AppleMusic__TeamId`, `__KeyId` | From the MusicKit key in the Apple Developer account. Empty disables Apple Music (`503 music_unavailable`). |
| `MusicProviders__AppleMusic__PrivateKey` | Secret. The `.p8` PEM text. A malformed key stops startup. |
| `MusicProviders__AppleMusic__DeveloperTokenLifetime` | Optional, default 12 hours (Apple allows up to 6 months). |
| `MusicProviders__Tidal__ClientId` | From the TIDAL developer dashboard. Not a secret. Empty disables Tidal. |
| `MusicProviders__Tidal__RedirectUris` | Comma-separated redirect URIs the app may send; each registered in the dashboard too. |
| `MusicProviders__Tidal__CountryCode` | Optional, default `US`. |
| `MusicProviders__SyncEvery`, `__ManualSyncInterval`, `__MaxLibraryItems`, `__MaxPlaylists`, `__MaxPlaylistItems` | Optional overrides (1 day, 15 minutes, 5 000, 50, 500). |
| `Spotify__TokenKey` | Required for either provider: stored tokens use it (see "Token storage"). |
