# Apple Music and Tidal (fan app)

Connect, sync, and disconnect the fan's Apple Music and Tidal accounts as listening sources next to Spotify
(`MusicScreen`, route `/music`, reached from the profile's **Other music services**). Same shape as the Spotify
feature: the app never holds a provider token; the API stores them encrypted and does the syncing
([ADR 0007](../../../../../docs/adr/0007-music-providers.md), openapi `/me/music`).

## Files
| File | Contents |
| --- | --- |
| `musicConnect.ts` | Types from the generated schema (`MusicStatus`, `MusicConnect`), `describeStatus`, `isSyncing`, `connectErrorMessage` (the `music_*` problem codes and the `code` / `music_user_token` field errors), `TIDAL_SCOPES`, `tidalAuthorizeUrl`, `completeTidalConnect` (state check, then `PUT /me/music/tidal`), `connectAppleMusicWith` (the Apple Music steps both platforms share: `GET /me/music/apple_music/developer-token`, the platform's MusicKit authorize, then `PUT /me/music/apple_music`). |
| `tidalRedirect.ts` | Web: full-page redirect to `https://login.tidal.com/authorize` with PKCE (helpers from `../spotify/pkce.ts`) and back to `<origin>/tidal-callback`; the attempt waits in `sessionStorage` (`fandom-score.tidal-connect`). |
| `tidalRedirect.native.ts` | iOS/Android: Tidal opens in an expo-web-browser auth session and returns on `fandomscore://tidal-callback`; the flow finishes in place. |
| `appleMusicConnect.ts` | Web: loads MusicKit JS v3 once (`https://js-cdn.music.apple.com/musickit/v3/musickit.js`, `musickitloaded`), `MusicKit.configure` + `music.authorize()` inside `connectAppleMusicWith`. Backing out of Apple's sign-in is `cancelled`. |
| `appleMusicConnect.native.ts` | iOS: `authorizeAppleMusic` from the local MusicKit module (below) inside `connectAppleMusicWith`; not allowing access is `cancelled`, a token MusicKit won't issue (no subscription) is explained. Android: says to connect from the web app. |
| `../../../modules/apple-music-kit/` | Local Expo module (autolinked from `modules/`, `expo-module.config.json`): `ios/AppleMusicKitModule.swift` wraps Apple's MusicKit (`MusicAuthorization.request()`, then `DefaultMusicTokenProvider().userToken(for:options:)`); `index.ts` exports `authorizeAppleMusic(developerToken)` (the Music User Token, or `null` when not allowed), `appleMusicAuthorizationStatus()`, `isAppleMusicKitAvailable()`. Where the native module doesn't exist (Android, web, Expo Go, Jest) they answer `null` / `unavailable` / `false`. |
| `MusicScreen.tsx` | One section per provider (Apple Music, then Tidal): status, Connect / Connect again, Sync now, Disconnect with a confirm step. Loads `GET /me/music`; polls `GET /me/music/{provider}` every 3 s while that provider syncs. |
| `MusicSummary.tsx` | "Apple Music: connected · Tidal: not connected" on the profile page. |
| `../../app/music.tsx`, `../../app/tidal-callback.tsx` | The routes (signed-in only; the callback sends the code once the session is restored, then goes to `/music`). |

## Setup
**Tidal.** `EXPO_PUBLIC_TIDAL_CLIENT_ID` (`TIDAL_CLIENT_ID` in the hosted image's environment) must be the API's
`MusicProviders__Tidal__ClientId`. The redirect URIs `<origin>/tidal-callback` (e.g. `http://localhost:8081/tidal-callback`
or `https://<tunnel>/tidal-callback`) and `fandomscore://tidal-callback` must be listed in the API's
`MusicProviders__Tidal__RedirectUris` **and** registered for the app in the TIDAL developer dashboard. Scopes:
`user.read collection.read playlists.read`. The web flow needs a secure context for `crypto.subtle` (HTTPS, or
`http://127.0.0.1` / `localhost`). Empty client ID: the section says Tidal isn't set up for this build.

**Apple Music.** `EXPO_PUBLIC_APPLE_MUSIC_ENABLED=true` (`APPLE_MUSIC_ENABLED` in the hosted image) shows the
button. Both platforms need the API's MusicKit key (`MusicProviders__AppleMusic__TeamId`, `__KeyId`,
`__PrivateKey`), which signs the developer token; without it the API answers `503 music_unavailable` and the screen
says so. The fan needs an Apple Music subscription to authorize.
- **Web:** MusicKit JS, loaded from Apple's CDN when the fan presses Connect.
- **iOS:** the local Expo module `modules/apple-music-kit` wraps Apple's MusicKit framework (iOS 15+; the app
  targets 16.4). It is compiled into every iOS build with native modules (development, beta, production), not Expo
  Go. `app.json` sets `NSAppleMusicUsageDescription`, the purpose string the system prompt shows. Needs the Apple
  Developer Program (MusicKit's "Media Services" identifier and key, which the API signs with) and an iOS 15+ device
  for the prompt and the token; a simulator has no Apple Music account. **Not compiled or run on a device yet:**
  the Swift is written against the MusicKit API as documented and checked only by `expo prebuild` (the module is
  autolinked as pod `AppleMusicKit`) and the Jest tests, which mock the native module.
- **Android:** Apple has no MusicKit for third-party Android apps, so the screen shows a note to connect from the
  web app instead of a button. A connection made anywhere counts everywhere, since it lives on the server.

Nothing in `app.json` changes for Tidal: `fandomscore://tidal-callback` uses the app's existing `fandomscore` scheme.

## Known limitations
- Neither flow has run against the real providers yet (ADR 0007, "Not verified"); the tests fake the API, MusicKit,
  and the auth session.
- The iOS MusicKit module (`modules/apple-music-kit`) has not been compiled or run on a device; the first iOS build
  with it may need Swift fixes. There is no Android MusicKit for third-party apps, so Android stays web-only.
- Syncing a provider manually is limited to once per 15 minutes per provider (the API's `429 rate_limited`).
