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
   the same account. For real Google sign-in, see [Sign in with Google](#sign-in-with-google).

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

## Sign in with Google
The app sends the fan to Google's account chooser and gets an OpenID Connect ID token back in the URL fragment
(`/google-callback`), which it sends to `POST /v1/auth/sessions`; no Google script runs on the page and no
library is needed. The API checks the token's signature, audience, and nonce (`docs/adr/0003-authentication.md`).
1. Google Cloud console → APIs & Services → Credentials → create an OAuth client of type **Web application**.
   Authorized JavaScript origins: `http://localhost:8081` (and your tunnel origin). Authorized redirect URIs:
   `http://localhost:8081/google-callback` (and `https://<tunnel>/google-callback`). Google allows plain `http`
   only for `localhost`, so on a phone use the HTTPS tunnel.
2. While the OAuth consent screen is in **Testing**, only the test users listed there can sign in (up to 100);
   anyone else sees "access blocked". Add each tester, or publish the screen as External.
3. Repo root `.env`: `Auth__Google__ClientIds=<web client ID>`. `apps/mobile/.env.local`:
   `EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID=<the same ID>`. Empty hides the button.
4. Sign-in screen → **Continue with Google**. A new account is asked for a handle (prefilled from the email).
   An email that already belongs to another account is refused (one email per account, ADR 0003).

The native app uses a separate **iOS** client ID (`EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID`): Google's page opens in an
auth session, returns a code on the reversed-client-ID scheme (`com.googleusercontent.apps.<id>:/oauth2redirect`,
add that scheme to `app.json`), and the app exchanges it with PKCE (`googleSignIn.native.ts`). List both client
IDs in `Auth__Google__ClientIds`. Not yet run on a device (no iOS build yet).

## Sign in with Apple
`expo-apple-authentication` on iOS: Apple's sheet returns an identity token, which goes to the same endpoint with
the raw nonce (Apple gets its SHA-256). Deleting an Apple account first shows the sheet again and sends the fresh
authorization code so the API can revoke the sign-in (App Store rule). Needs the Apple Developer Program, the
bundle ID in `Auth__Apple__ClientIds`, and an iOS build; the button is hidden on the web and Android. Apple's web
flow (Services ID plus a server-side form post) is not built.

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
`build:web` clears Metro's cache first: a cached module can otherwise keep `EXPO_PUBLIC_` values from your local
dev server and ship them in the build. `dist/` is a single-page app: the host must serve `index.html` for every path
(e.g. `/profile`). Cloudflare
Pages and Firebase Hosting do this with a rewrite rule; `python -m http.server` does not.

Beta hosts it at `https://app.beta.firstline.com`: the `Dockerfile` target `web` builds it and serves it with nginx
(`infrastructure/README.md` "Web apps"). That image is built without `EXPO_PUBLIC_` values; the settings come
at startup from `/config.js`, which nginx writes from its environment, so one image serves beta and prod
(`src/config.ts`). Locally nothing writes `/config.js` and the build-time values apply.

## Layout
| Path | Contents |
| --- | --- |
| `src/app/` | Routes (expo-router): `index` (routing by session state), `sign-in`, `google-callback`, `profile`, `spotify`, `spotify-callback`. |
| `src/auth/` | `SessionManager` (tokens, single-flight refresh, sign-in/out, deletion), `SessionProvider`, token storage. |
| `src/api/` | `schema.d.ts` (generated, don't edit), `client.ts` (typed client, `ApiError`), `errors.ts` (messages). |
| `src/features/<feature>/` | Feature UI, e.g. `authentication/SignInScreen` (Google, Apple, developer sign-in), `settings/ProfileScreen`. |
| `src/ui/` | Shared components and theme tokens. |

## Commands
```bash
npm run web            # dev server with hot reload
npm test               # Jest (jest-expo)
npm run typecheck      # tsc
npm run generate:api   # regenerate src/api/schema.d.ts after openapi.yaml changes (CI checks it)
npm run build:web      # static web build in dist/
e2e/run.sh             # end-to-end journeys in a browser against the API and a seeded database (e2e/README.md)
```

## How sessions work
- Access token (15 minutes) in memory; refresh token (and which provider signed in) in `localStorage` on web, the
  Keychain on iOS (`tokenStore.native.ts`).
- Every API call goes through `session.api`. On a 401 it refreshes once and retries. Refreshes are
  single-flight, because the API ends the whole session when a refresh token is used twice.
- Known limit on web: two open tabs can both refresh with the same token, which signs both out.
- Every route under `src/app` must show `<Loading />` while `state.status === 'loading'` and only then decide to
  redirect. On a reload the session is still restoring, and an early redirect sends the fan home from a deep link.
  `src/routeGuards.test.tsx` checks every route. Don't put test files under `src/app`: expo-router makes each
  file there a route and bundles it.

## Moving to iOS
Needs the Apple Developer Program ($99/year). Then:
1. Pick the bundle ID and add `ios.bundleIdentifier` to `app.json`.
2. Sign in with Apple and Google are built (see above); add the Google iOS client's scheme to `app.json` and try
   both on a device.
3. Build in the cloud with EAS (`eas build -p ios`), install a development build or ship to TestFlight
   (`eas submit`). No Mac needed. Everything else (screens, session, API client) is shared.

## Not done yet
Sign in with Apple and Google in the native app are written but not run on a device (needs the Apple program and
an iOS build); Apple's web flow; Spotify connect in the native app.
