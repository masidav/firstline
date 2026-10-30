# TODO — Artist search mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build a search screen with debounce to reduce provider/API requests.
- [x] Show loading, empty, error, and retry states. (Verified 2026-10-30 by `ArtistSearch.test.tsx`: "Looking on
      Spotify…", no match here / on Spotify, a failed `GET /artists` recovering on the next keystroke, Spotify down
      with Enter retrying.)
- [x] Display artist identity and tracked/onboarding/active status.
- [x] Allow adding untracked artists and prevent repeated taps while the request is pending. (Tested 2026-10-30: every
      Add is off while one is pending; a second tap sends nothing.)
- [ ] Display the daily limit message and onboarding progress. *Limit message done and tested (HomeScreen and
      ArtistSearch tests: after the message, Add stays available and the next tap asks again); progress waits on the
      onboarding worker.*
- [ ] Navigate to the artist page when an artist is already tracked. *Partly: an artist already here opens their
      albums from the typed-search list (tested 2026-10-30). A Spotify result marked "Already here" is not yet a link.*
- [ ] Refresh status when onboarding completes.
- [x] Add component/screen tests for search, add, limit, and failure cases. (`ArtistSearch.test.tsx` 9 tests added
      2026-10-30, beside the 7 search tests in `HomeScreen.test.tsx`.)

The search lives on the home screen (`src/features/home/HomeScreen.tsx`): typing filters artists already here
(`GET /artists`); Enter with no exact match asks Spotify (`GET /artists/search`) and offers Add (`POST /artists`).

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-30: `npm test` 51 suites, 327 tests (ArtistSearch 9,
      HomeScreen 17), `npm run typecheck` clean. 2026-10-21: `npm test` 33/33 (HomeScreen 6), `npm run typecheck` clean.
      2026-10-21: clicked through in Chrome against the local stack and real Spotify (search, Enter, Add, then
      the added artist shows as "Getting ready").
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
