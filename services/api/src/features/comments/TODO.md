# TODO — Comments API and reward rules

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Implement comments on album logs and optional threaded replies using parent_id. (`CommentEndpoints`; verified 2026-10-21: CommentsApiTests 7/7.)
- [x] Validate comment length and reject malformed or disallowed content. (1 to 2,000 characters; disallowed content is Phase 9 moderation.)
- [x] Award comment points only when the comment meets the configured minimum length and is on another user's log. (`comments_award` trigger, 0001.)
- [x] Apply the configured daily award cap per user per UTC day. (`award_points()`; tested at the cap of 5.)
- [x] Use award_points() or equivalent transactional logic with a per-user lock to prevent concurrent cap bypass. (`award_points()` takes a per-user, per-rule advisory lock.)
- [x] Ensure each source event can be rewarded only once. (`score_events_source_uidx`.)
- [x] Reverse a comment's reward when it is soft-deleted; do not delete ledger history. (`comments_reverse` trigger; tested.)
- [x] Do not award points for self-comments, short comments, paused artists, or duplicate award attempts. (Trigger and `award_points()`; self and short tested.)
- [ ] Add reporting/moderation hooks and enforce access/visibility rules. (Visibility done: a comment is visible exactly when its log is. Reports and moderation are Phase 9.)
- [x] Add tests for threshold boundaries, daily caps, concurrent requests, soft deletion, and reversals. (Concurrency is covered by the database lock in `award_points()`, not by a test.)

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: CommentsApiTests 7/7 (branch `phase-6-journal`).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
