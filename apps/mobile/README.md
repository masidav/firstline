# Fan app (web and iOS)

One Expo (React Native) codebase. It runs as a **web app** today, so it can be tried on a phone without an
Apple Developer account. The same code is the iOS app: its native project is configured and checked in CI, and
building it needs the Apple Developer Program (see [Building the iOS app](#building-the-ios-app)).

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

Beta hosts it at `https://app.beta.firstline-5730kl.com`: the `Dockerfile` target `web` builds it and serves it with nginx
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
| `app.json`, `app.config.ts` | App config: the static part (name, bundle ID, icon, plugins) and the part that depends on the environment (Google's scheme, EAS project id, local-network exception, the personal-team build). |
| `eas.json` | EAS Build profiles: `development`, `development-simulator`, `beta`, `production`. |
| `assets/` | Icon and splash image, drawn by `tools/dev/app-icons.mjs` (placeholders until there is a brand). |

## Commands
```bash
npm run web            # dev server with hot reload
npm test               # Jest (jest-expo)
npm run typecheck      # tsc
npm run generate:api   # regenerate src/api/schema.d.ts after openapi.yaml changes (CI checks it)
npm run build:web      # static web build in dist/
npm run check:ios      # generates the iOS project (ios/, not committed) and bundles the JS for iOS; CI runs it
npm run ios:personal   # on a Mac: build and install on a plugged-in iPhone with a free Apple ID (see below)
maestro test maestro/launch.yaml   # the launch smoke flow against a booted simulator with the app installed
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

## Building the iOS app
The native project is generated from `app.json` and `app.config.ts` (`expo prebuild`); nothing under `ios/` is
hand-edited or committed. What's set: bundle ID `com.firstline.fandomscore`, Sign in with Apple, push
(`aps-environment`), the photo-library purpose string, the `fandomscore` URL scheme (Spotify's redirect
`fandomscore://spotify-callback`), Google's reversed-client-ID scheme when `EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID` is
set, portrait only, iPhone only, and the export-compliance answer (`usesNonExemptEncryption: false`, the app only
uses HTTPS). CI runs `npm run check:ios` on every push: the prebuild and an iOS JavaScript bundle, which is as far
as Linux gets. The build itself happens in EAS Build's cloud, so no Mac is needed.

**Once, with the Apple Developer Program ($99/year) and an Expo account:**
1. `npm install -g eas-cli && eas login`, then in `apps/mobile`: `eas init` (creates the EAS project and writes
   `extra.eas.projectId` into `app.json`; commit that). The project id is also what push notifications need.
2. `eas credentials -p ios`: let EAS create the distribution certificate, the provisioning profile, and the push key
   (APNs) for `com.firstline.fandomscore`. EAS stores them; nothing goes in the repo.
3. Sign in with Apple: in the Apple developer portal create a Sign in with Apple key (`.p8`). The API needs
   `Auth__Apple__ClientIds=com.firstline.fandomscore`, the key, Team ID, and Key ID (`docs/OWNER_TODO.md`).
4. Google: create an OAuth client of type **iOS** for the bundle ID. Its ID goes in the build's environment
   (`eas env:create --name EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID --visibility plaintext`, one per environment) and in the
   API's `Auth__Google__ClientIds` next to the web client. `app.config.ts` adds the reversed scheme by itself.
5. Spotify: add `fandomscore://spotify-callback` to the Spotify app's redirect URIs and to the API's
   `Spotify__RedirectUris`; `EXPO_PUBLIC_SPOTIFY_CLIENT_ID` goes in the EAS environment like the Google ID.

**On your own iPhone with a free Apple ID (no Developer Program):** Xcode signs with a "personal team" and
installs on a phone you plug in; the app runs for 7 days, then rebuild. On a Mac with Xcode (App Store), its
command-line tools, and CocoaPods (`brew install cocoapods`):
```bash
cd apps/mobile && npm install
# Xcode → Settings → Accounts → add your Apple ID; the 10-character team ID is next to "Personal Team".
export APPLE_TEAM_ID=XXXXXXXXXX
EXPO_PUBLIC_API_URL=https://<tunnel>/v1 EXPO_PUBLIC_DEV_SIGN_IN=true npm run ios:personal
```
`ios:personal` regenerates `ios/` with `IOS_PERSONAL_TEAM=1`, builds with Xcode, installs on the connected phone
(unlock it and tap Trust; the first time, also Settings → General → VPN & Device Management → trust your Apple ID),
and starts the dev server. What's different in this build, because a personal team can't be provisioned for
them: no Sign in with Apple (the button is hidden) and no push notifications, and the bundle ID is
`com.firstline.fandomscore.dev` so the real one isn't registered to the personal team. Developer sign-in, Google
(with an iOS client ID for the `.dev` bundle ID), Spotify, the Keychain, share sheets, and the offline queue can all
be tried. Limits: 3 such apps per Apple ID and 10 devices a year.

**Development build (your API, sign-in as anyone):**
```bash
EXPO_PUBLIC_API_URL=https://<tunnel>/v1 eas build -p ios --profile development   # installs on registered devices
npx expo start --dev-client                                                      # then open the app on the phone
```
`eas device:create` registers a phone for internal distribution. The profile turns developer sign-in on. An API on
plain `http://192.168.x.x` works too (`app.config.ts` adds the local-networking exception), but Google, Apple, and
Spotify sign-in need HTTPS, so a tunnel is simpler. `--profile development-simulator` makes a simulator build for a
Mac.

**Beta (TestFlight):** `eas build -p ios --profile beta` builds against `https://api.beta.firstline-5730kl.com/v1` with the
build number incremented, then `eas submit -p ios --profile beta` uploads it to App Store Connect; add testers in
TestFlight. `production` is the same against the production API, whose hostname is still the placeholder from
`infrastructure/k8s/overlays/prod` (change both when it's decided).

**In the pipeline.** Two workflows, both started from the Actions tab (they don't run on push):
- *iOS app (simulator)* (`mobile-ios.yml`): a macOS runner generates the native project, builds an unsigned Release
  app for the iOS Simulator with the JavaScript bundle embedded, launches it in a simulator, and runs the Maestro
  flow in `maestro/launch.yaml` (the first screen renders). The `.app` is an artifact: open it in Xcode's Simulator
  or upload it to appetize.io. No Apple account involved. A run takes about 28 minutes, and macOS minutes cost ten
  times Linux minutes on a private repository, so it also runs weekly only if the repository variable
  `IOS_WEEKLY_BUILD` is `true`.
- *Mobile release (EAS)* (`mobile-release.yml`): `eas build` for iOS, Android, or both with a chosen profile, and
  optionally `--auto-submit` (TestFlight, Google Play testing tracks). Needs the repository secret `EXPO_TOKEN`
  (expo.dev → Account settings → Access tokens) and the one-time setup above; it stops with a message otherwise.

Not tried yet, in this order, once a build exists: Sign in with Apple and Google, connecting Spotify, push
notifications on a real phone, the share sheets, and the offline journal queue (Keychain and file storage are
written and unit-tested with the modules mocked). Universal links (`https://app.beta.firstline-5730kl.com/...` opening
the app) and EAS Update (over-the-air JavaScript fixes) are not set up.

## Not done yet
Everything native is written but not run on a device (needs the Apple program and an iOS build; see above). Apple's
web flow. Android is configured (`android.package`, adaptive icon) but has never been built.
