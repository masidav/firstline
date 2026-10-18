# TODO — Spotify mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build connect/disconnect Spotify UI using the API's OAuth flow. (Web: PKCE with a full-page redirect. iOS: `spotifyRedirect.native.ts` still to build.)
- [x] Explain the requested permissions and handle cancellation or denial.
- [x] Show connection state and last successful sync time.
- [x] Provide a manual sync action if supported and prevent duplicate requests.
- [x] Show sync progress and recoverable error states. (polls every 3 s while queued/running)
- [x] Handle reauthorization when refresh fails or scopes are missing.
- [x] Add tests for connect, cancel, disconnect, sync, and error states.
- [ ] Native (iOS) connect with expo-web-browser and a `fandomscore://spotify-callback` redirect.
- [ ] Try with a real Spotify account (allowlisted in the Spotify dashboard).

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-18: Jest (spotifyConnect, SpotifyScreen) and a Playwright WebKit run against the real API (Spotify itself simulated; real token endpoint rejected the test code as expected).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
