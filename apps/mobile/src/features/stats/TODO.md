# TODO — User stats UI

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Stats screen for the signed-in fan: per-artist scores first, then journal, proof, listening, points, and social totals.
      (`/stats`, from the profile's "Your stats" button. Social totals ("People": followers, following) added 2026-10-30.)
- [x] Same screen for another fan's profile, showing only what the API returns for the viewer.
      *Fan pages (`/fans/{handle}`, `features/profiles/FanProfileScreen`) show followers the top five artist standings,
      albums logged, reviews, verified proof, and points from `GET /users/{handle}/stats`; strangers get a follow prompt.*
- [x] Label estimated scores and explain what moves each number.
- [x] Empty states for new fans (no tracked artists, no Spotify, no logs).
- [x] Screen tests for own profile, another fan's profile, empty states, and estimated labels.
      *StatsScreen.test.tsx (totals incl. followers/following, empty state, retry); FanProfileScreen.test.tsx.*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: StatsScreen 3/3 (Jest); clicked through in Chrome
      (sign up, hub city, Profile, Your stats) against the local API. 2026-10-30: `npm run typecheck` clean; Jest
      301/301 (`npm test -- --ci`) after regenerating `schema.d.ts`. Not re-checked in a browser.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone (Phase 15, 2026-10-30).
