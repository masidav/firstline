# TODO — Fandom score mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build artist-specific score and percentile presentation. (`ScoreScreen`, 2026-10-24.)
- [x] Display the estimated label whenever the API marks the score estimated.
- [x] Explain that percentiles rank app users blended with a synthetic baseline, not every real-world fan.
- [x] Show loading, unavailable, onboarding, and error states. (Plus computing, with polling and Refresh.)
- [x] Refresh score data after relevant sync or journal activity. (Refresh button; computing scores poll. No push from the server.)
- [ ] Link to the share-score-card flow. *Waits for the Phase 8 sharing feature.*
- [x] Add tests for estimated/final labels, missing scores, and paused artists.

## Verification
- [x] Add or update automated tests for the behavior above. (`score.test.ts`, `ScoreScreen.test.tsx`.)
- [x] Run the relevant tests and record actual results. 2026-10-24: fan app typecheck clean, jest all suites green.
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
