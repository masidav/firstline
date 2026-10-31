# Spotify (fan app)

Connect, sync, and disconnect the fan's Spotify account (`SpotifyScreen`, route `/spotify`). The app never holds a
Spotify token or client secret: it runs Spotify's PKCE authorization and sends the code to `PUT /v1/me/spotify`,
and the API exchanges it ([ADR 0005](../../../../../docs/adr/0005-spotify-integration.md)).

## Connect flow
- **Web** (`spotifyRedirect.ts`): a full-page redirect to Spotify and back to `/spotify-callback`, which checks the
  state and sends the code. Needs a secure context (HTTPS, or `http://127.0.0.1` / `localhost`) for `crypto.subtle`.
  Redirect: `<origin>/spotify-callback`.
- **iOS/Android** (`spotifyRedirect.native.ts`): Spotify opens in an in-app auth session (expo-web-browser) and
  returns on `fandomscore://spotify-callback`; the flow finishes in place. PKCE uses expo-crypto. Closing the
  browser cancels quietly.

Every redirect URI must be registered in the Spotify dashboard and listed in the API's `Spotify__RedirectUris`.
Development-mode Spotify apps only allow accounts added under Users in the dashboard (5 at most).

## Buttons on songs and albums
`NowPlaying.tsx` holds the now-playing bar and Play, Queue, and Play album (through `GET`/`PUT`/`POST /me/spotify/player...`);
`Library.tsx` holds the Like buttons (`/me/spotify/library`), asking Spotify once per screen which songs and albums
are saved; `SongControls.tsx` puts Play, Queue, and Like together wherever a song appears. Without a Spotify
connection only Play shows (it opens the song in Spotify); with a connection made before the library permission
existed, Like asks the fan to connect again.

## Configuration
`EXPO_PUBLIC_SPOTIFY_CLIENT_ID` (the dashboard's client ID; not a secret). Without it the screen says Spotify isn't
set up for this build.

## Known limitations
- The native flow has not run on a device yet (needs a development build).
