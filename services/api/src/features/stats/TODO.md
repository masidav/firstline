# TODO — User stats API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define the stats response in `openapi.yaml` (`GET /me/stats`, `GET /users/{userId}/stats`): per-artist scores, journal, proof, listening, points, and social totals (Phase 15 in `PROJECT_TODO.md`).
- [ ] Compute from stored tables and the ledger only (`user_artist_scores`, `user_artist_signals`, `album_logs`, `lists`, `comments`, `submissions`, `score_events`); never call Spotify.
- [ ] Exclude soft-deleted logs and comments and reversed ledger rows from every count.
- [ ] Enforce visibility: own stats in full; others per profile visibility and follow relationship; nothing for blocked fans.
- [ ] Label estimated scores (baseline weight still high) in the response.
- [ ] Keep the query cheap: one round trip, indexed by user; add a materialised summary only if measured to be slow.
- [ ] Add tests for counts after reversals and deletions, visibility, blocked fans, and a fan with no data.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
