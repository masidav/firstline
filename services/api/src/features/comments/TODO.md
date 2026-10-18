# TODO — Comments API and reward rules

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Implement comments on album logs and optional threaded replies using parent_id.
- [ ] Validate comment length and reject malformed or disallowed content.
- [ ] Award comment points only when the comment meets the configured minimum length and is on another user's log.
- [ ] Apply the configured daily award cap per user per UTC day.
- [ ] Use award_points() or equivalent transactional logic with a per-user lock to prevent concurrent cap bypass.
- [ ] Ensure each source event can be rewarded only once.
- [ ] Reverse a comment's reward when it is soft-deleted; do not delete ledger history.
- [ ] Do not award points for self-comments, short comments, paused artists, or duplicate award attempts.
- [ ] Add reporting/moderation hooks and enforce access/visibility rules.
- [ ] Add tests for threshold boundaries, daily caps, concurrent requests, soft deletion, and reversals.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
