# Native journeys (Maestro)

The fan app's critical journeys in the native app, against the real API and a real, seeded database: the same
ground as the browser suite (`e2e/`), on Android. CI: `.github/workflows/mobile-android.yml` ("Android app
(emulator)"), on pushes to `main` that touch the app, the API, the database, or the seed, and on demand.

| Flow | Journey |
| --- | --- |
| `journeys/signup.yaml` | Sign up: developer sign-in, a handle, a hub city (Chicago), home. |
| `journeys/home.yaml` | Home loads: artist search, tabs, and feed cards from the API. |
| `journeys/journal.yaml` | Find an artist, open an album, log it with a rating and review, earn points, find it in the journal. |
| `journeys/allocation.yaml` | Open a show by deep link, register for its fan allocation, still registered after an app restart, withdraw. |
| `journeys/account.yaml` | Sign out and back in to the same account; delete the account; the same name then starts a new account. |
| `launch.yaml` | Smoke flow for the iOS Simulator build (`mobile-ios.yml`): the first screen renders. |

`common/` holds subflows (a clean launch, signing in as an existing account); `maestro test` on the `journeys`
directory doesn't run them on their own.

## Run it
```bash
# A Release build that talks to the API on this machine (the emulator reaches it through `adb reverse`):
cd apps/mobile
EXPO_PUBLIC_API_URL=http://127.0.0.1:5271/v1 EXPO_PUBLIC_DEV_SIGN_IN=true EXPO_NO_DOTENV=1 \
  npx expo prebuild --platform android --clean --no-install
(cd android && ./gradlew :app:assembleRelease -PreactNativeArchitectures=x86_64)

# With an emulator running (Android 14, x86_64; `emulator -avd <name>`):
MAESTRO_APK=android/app/build/outputs/apk/release/app-release.apk maestro/run.sh
MAESTRO_FLOWS=maestro/journeys/journal.yaml maestro/run.sh        # one flow (the app already installed)
```
Needs JDK 17, the Android SDK (platform 36, build tools 36, NDK 27.1, an x86_64 system image) with `adb` and the
emulator on `PATH`, KVM, Docker, the .NET SDK, Node, Python 3, and the
[Maestro CLI](https://github.com/mobile-dev-inc/maestro/releases) (CI pins the version and checksum). `run.sh` starts
PostgreSQL 16 in Docker (or uses `E2E_DATABASE_URL`), applies migrations, loads `tools/dev/seed.py --fake`, starts
the API from source with developer sign-in on, and runs `setup.mjs`. Output, including screenshots of failures and
the API log, goes to `maestro-output/` (CI uploads it as `maestro-android`).

`EXPO_PUBLIC_API_URL` on `http://` is what lets the build use plain HTTP (`app.config.ts`: cleartext traffic on
Android, the local-networking exception on iOS); builds for https APIs get neither.

## How the flows are written
- Elements are found by `testID` (`id:` in Maestro; a resource id on Android, an accessibility identifier on iOS),
  not by visible text, so copy changes don't break them. Ids are kebab-case and named for what they are
  (`dev-sign-in-name`, `log-save`, `tab-journal`); rows carry their record id (`artist-<id>`, `album-<id>`).
- `setup.mjs` makes what the flows need through the API, as staff do, and writes what only a background job would
  (a fandom score) to the database. Every run uses new accounts, so the flows can run again on the same database.
  Its `KEY=value` output is passed to Maestro with `-e` (`${HOME_FAN}`, `${SHOW_ID}`, ...).
- `run.sh` hides Android's "isn't responding" and "has stopped" dialogs (`hide_error_dialogs`): the emulator's own
  Pixel Launcher hung during the flows twice in CI and its dialog covered the app, failing every flow.
- Each flow starts from a cleared app (`common/launch-signed-out.yaml`), retried because the emulator now and then
  fails to start the app process right after its data is cleared.

Not covered: the offline journal queue (the browser suite covers it), Google and Apple sign-in (real providers),
push notifications (no Firebase project yet), and staff screens. The flows are written to run on iOS too, but the
iOS workflow builds against beta's API without developer sign-in, so there it runs only `launch.yaml`.
