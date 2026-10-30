# TODO — User stats API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Define the stats response in `openapi.yaml` (`GET /me/stats`, schema `UserStats`, 2026-10-21; `GET /users/{handle}/stats`,
      schema `ProfileStats`, 2026-10-25; `social` block with followers and following added 2026-10-30, so every stat
      Phase 15 lists is in the response).
- [x] Compute from stored tables
- [x] Exclude soft-deleted logs
- [x] Enforce visibility: own stats in full; others per follow relationship (followers-only, none public by default,
      decided 2026-10-25): the fan and their followers get `ProfileStats` (no listening block; private logs and lists
      not counted), anyone else `403 followers_only`, a deleted or unknown handle `404 not_found`. Blocking isn't
      wanted for now (decided 2026-10-24), so there is nothing to check for blocked fans.
- [x] Label estimated scores
- [x] Keep the query cheap
- [x] Add tests for counts after reversals and deletions, visibility, blocked fans, and a fan with no data.
      *StatsApiTests: no data; deleted logs and comments (including a comment on a log deleted afterwards, fixed
      2026-10-30), reversed awards, unverified proof, inactive artists; a log deleted through the API (count drops,
      award reversed, ledger rows kept); follower / stranger / removed follower / the fan / deleted account. Blocked
      fans: no blocking feature exists (see above).*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: StatsApiTests 3/3; full suite 157/157.
      2026-10-30: StatsApiTests 5/5, ProfileApiTests 1/1, FeedApiTests pass; full suite result in `PROJECT_TODO.md`.
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations (`README.md`, 2026-10-30).
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone (Phase 15, 2026-10-30).
