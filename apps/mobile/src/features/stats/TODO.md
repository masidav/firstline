# TODO — User stats UI

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Stats screen for the signed-in fan: per-artist scores first, then journal, proof, listening, points, and social totals.
      (`/stats`, from the profile's "Your stats" button. Social totals wait on Phase 8.)
- [ ] Same screen for another fan's profile, showing only what the API returns for the viewer.
- [x] Label estimated scores and explain what moves each number.
- [x] Empty states for new fans (no tracked artists, no Spotify, no logs).
- [ ] Screen tests for own profile, another fan's profile, empty states, and estimated labels.

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: StatsScreen 3/3 (Jest); clicked through in Chrome
      (sign up, hub city, Profile, Your stats) against the local API.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
