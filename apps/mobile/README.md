# Fan app (web now, iOS later)

One Expo (React Native) codebase. It runs as a **web app** today, so it can be tried on a phone without an
Apple Developer account. The same code builds the iOS app later (see [Moving to iOS](#moving-to-ios)).

Expo SDK 57, expo-router, TypeScript. The API client is generated from `openapi.yaml`.

## Run it on your computer
1. Start the API with developer sign-in (repo root, see `docs/DEVELOPMENT.md`). In the repo root `.env`:
   `Auth__DevSignIn=true` and `Cors__AllowedOrigins=http://localhost:8081`. Then:
   ```bash
   set -a; . ./.env; set +a
   dotnet run --project database && dotnet run --project services/api
   ```
2. Start the app (Node 22):
   ```bash
   cd apps/mobile
   cp .env.example .env.local        # API URL and developer sign-in switch
   npm install
   npm run web                       # http://localhost:8081
   ```
3. Sign in with any test name (e.g. `alice`). The first time, choose a handle. The same name signs back in to
   the same account.

## Try it on your phone
**Same Wi-Fi (quickest).** Find your computer's LAN address (e.g. `192.168.1.20`), then:
- API: run it on all interfaces: `ASPNETCORE_URLS=http://0.0.0.0:5271 dotnet run --project services/api`, with
  `Cors__AllowedOrigins=http://192.168.1.20:8081`.
- App: in `.env.local`, `EXPO_PUBLIC_API_URL=http://192.168.1.20:5271/v1`; run `npm run web`.
- On the phone, open `http://192.168.1.20:8081` in Safari, then Share → **Add to Home Screen** to launch it
  full screen like an app.

**From anywhere (HTTPS).** Use a tunnel, e.g. Cloudflare Tunnel (`cloudflared tunnel --url http://localhost:5271`
for the API and another for port 8081). Put the API tunnel URL in `EXPO_PUBLIC_API_URL` (plus `/v1`) and the app
tunnel URL in `Cors__AllowedOrigins`. Google sign-in will need HTTPS anyway.

Developer sign-in lets anyone who can reach your API sign in as anyone, so only expose a development database.

## Connect Spotify
Spotify only redirects back to HTTPS addresses or the literal `http://127.0.0.1` (not `localhost`), so for
Spotify open the app at **http://127.0.0.1:8081**.
1. Spotify developer dashboard → your app: add the redirect URI `http://127.0.0.1:8081/spotify-callback`, and
   your Spotify account under **Users** (development mode allows allowlisted accounts only).
2. Repo root `.env`: `Spotify__ClientId`, `Spotify__TokenKey`, and
   `Spotify__RedirectUris=http://127.0.0.1:8081/spotify-callback`; add `http://127.0.0.1:8081` to
   `Cors__AllowedOrigins`. Run the API **and the worker** (`dotnet run --project workers`), which does the sync.
3. `apps/mobile/.env.local`: `EXPO_PUBLIC_SPOTIFY_CLIENT_ID` (same client ID) and
   `EXPO_PUBLIC_API_URL=http://127.0.0.1:5271/v1`.
4. Profile → **Connect Spotify** → Spotify's consent page → back in the app, the first sync runs.

On a phone, use an HTTPS tunnel URL instead and register `https://<tunnel>/spotify-callback` the same way.
Plain `http://192.168.x.x` can't connect Spotify (no secure context for PKCE, and Spotify rejects the URI).

How it works: the app makes a PKCE verifier, sends the browser to Spotify, and on `/spotify-callback` checks
the `state` matches what this tab started before sending `code` + `code_verifier` to `PUT /v1/me/spotify`. The
server does the exchange; no Spotify token ever reaches the browser. Code: `src/features/spotify/`.

## Publish a static build
```bash
EXPO_PUBLIC_API_URL=https://api.example.com/v1 npm run build:web   # writes dist/
```
`dist/` is a single-page app: the host must serve `index.html` for every path (e.g. `/profile`). Cloudflare
Pages and Firebase Hosting do this with a rewrite rule; `python -m http.server` does not.

## Layout
| Path | Contents |
| --- | --- |
| `src/app/` | Routes (expo-router): `index` (routing by session state), `sign-in`, `profile`, `spotify`, `spotify-callback`. |
| `src/auth/` | `SessionManager` (tokens, single-flight refresh, sign-in/out, deletion), `SessionProvider`, token storage. |
| `src/api/` | `schema.d.ts` (generated, don't edit), `client.ts` (typed client, `ApiError`), `errors.ts` (messages). |
| `src/features/<feature>/` | Feature UI, e.g. `authentication/DevSignInForm`, `settings/ProfileScreen`. |
| `src/ui/` | Shared components and theme tokens. |

## Commands
```bash
npm run web            # dev server with hot reload
npm test               # Jest (jest-expo)
npm run typecheck      # tsc
npm run generate:api   # regenerate src/api/schema.d.ts after openapi.yaml changes (CI checks it)
npm run build:web      # static web build in dist/
```

## How sessions work
- Access token (15 minutes) in memory; refresh token in `localStorage` on web, the Keychain on iOS
  (`tokenStore.native.ts`).
- Every API call goes through `session.api`. On a 401 it refreshes once and retries. Refreshes are
  single-flight, because the API ends the whole session when a refresh token is used twice.
- Known limit on web: two open tabs can both refresh with the same token, which signs both out.

## Moving to iOS
Needs the Apple Developer Program ($99/year). Then:
1. Pick the bundle ID and add `ios.bundleIdentifier` to `app.json`.
2. Add Sign in with Apple (`expo-apple-authentication`) next to developer sign-in; on account deletion, get a
   fresh Apple authorization code and pass it to `session.deleteAccount(code)`.
3. Build in the cloud with EAS (`eas build -p ios`), install a development build or ship to TestFlight
   (`eas submit`). No Mac needed. Everything else (screens, session, API client) is shared.

## Not done yet
Google sign-in (needs a Google OAuth web client ID), Sign in with Apple (needs the Apple program), Spotify connect
in the native app, and the feature screens beyond profile and Spotify (artists, scores, journal).
