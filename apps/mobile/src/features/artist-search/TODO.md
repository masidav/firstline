# TODO — Artist search mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build a search screen with debounce to reduce provider/API requests.
- [ ] Show loading, empty, error, and retry states.
- [x] Display artist identity and tracked/onboarding/active status.
- [x] Allow adding untracked artists and prevent repeated taps while the request is pending.
- [ ] Display the daily limit message and onboarding progress. *Limit message done; progress waits on the onboarding worker.*
- [ ] Navigate to the artist page when an artist is already tracked.
- [ ] Refresh status when onboarding completes.
- [x] Add component/screen tests for search, add, limit, and failure cases.

The search lives on the home screen (`src/features/home/HomeScreen.tsx`): typing filters artists already here
(`GET /artists`); Enter with no exact match asks Spotify (`GET /artists/search`) and offers Add (`POST /artists`).

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: `npm test` 33/33 (HomeScreen 6), `npm run typecheck` clean.
      2026-10-21: clicked through in Chrome against the local stack and real Spotify (search, Enter, Add, then
      the added artist shows as "Getting ready").
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
