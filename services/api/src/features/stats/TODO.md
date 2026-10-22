# TODO — User stats API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Define the stats response in `openapi.yaml` (`GET /me/stats`, schema `UserStats`, 2026-10-21; `GET /users/{handle}/stats` waits on Phase 8).
- [x] Compute from stored tables
- [x] Exclude soft-deleted logs
- [ ] Enforce visibility: own stats in full; others per profile visibility and follow relationship; nothing for blocked fans.
      *Own stats only for now: follows, blocks, and profile visibility don't exist yet (Phase 8).*
- [x] Label estimated scores
- [x] Keep the query cheap
- [ ] Add tests for counts after reversals and deletions, visibility, blocked fans, and a fan with no data.
      *Reversals, deletions, and a fan with no data are covered; visibility and blocks wait on Phase 8.*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: StatsApiTests 3/3; full suite 157/157.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
