# TODO — Spotify mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build connect/disconnect Spotify UI using the API's OAuth flow. (Web: PKCE with a full-page redirect. Native: an in-app auth session, `spotifyRedirect.native.ts`.)
- [x] Explain the requested permissions and handle cancellation or denial.
- [x] Show connection state and last successful sync time.
- [x] Provide a manual sync action if supported and prevent duplicate requests.
- [x] Show sync progress and recoverable error states. (polls every 3 s while queued/running)
- [x] Handle reauthorization when refresh fails or scopes are missing.
- [x] Add tests for connect, cancel, disconnect, sync, and error states.
- [ ] Native (iOS) connect with expo-web-browser and a `fandomscore://spotify-callback` redirect. *Built 2026-10-21*
      (expo-web-browser auth session, expo-crypto PKCE; Jest 5 tests with the RFC 7636 vector; `expo export --platform ios`
      bundles). Not yet run on a device: needs a dev build (Apple Developer Program), and the redirect registered in the
      Spotify dashboard and added to `Spotify__RedirectUris`.
- [ ] Try with a real Spotify account (allowlisted in the Spotify dashboard).
- [x] Now-playing bar under every screen and Play buttons on songs (requested 2026-10-26; `NowPlaying.tsx`, `player.ts`).
      Polls `GET /me/spotify/player` every 15 s while the app is open (60 s when not connected); a failed play opens the
      song in Spotify and the bar says why. Verified: NowPlaying tests 7, typecheck. Not yet tried with real Spotify.
- [x] Quiet sync when the app opens (`backgroundSync.ts`, once per launch, `?background=true`) and a "Resync Spotify"
      button on the profile (decided 2026-10-25). Verified: backgroundSync and ProfileScreen tests.

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-18: Jest (spotifyConnect, SpotifyScreen) and a Playwright WebKit run against the real API (Spotify itself simulated; real token endpoint rejected the test code as expected).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
