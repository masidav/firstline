# TODO — User stats UI

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Stats screen for the signed-in fan: per-artist scores first, then journal, proof, listening, points, and social totals.
- [ ] Same screen for another fan's profile, showing only what the API returns for the viewer.
- [ ] Label estimated scores and explain what moves each number.
- [ ] Empty states for new fans (no tracked artists, no Spotify, no logs).
- [ ] Screen tests for own profile, another fan's profile, empty states, and estimated labels.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
