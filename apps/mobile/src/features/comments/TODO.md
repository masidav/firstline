# TODO — Comments mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Display comments and replies on album logs with pagination if needed. (`CommentsSection` on the log screen, replies indented one level; first 100 comments. Verified 2026-10-21: `npm test` CommentsSection 4.)
- [x] Allow adding replies and show validation errors before submission. (Empty drafts are caught in the form; API field errors are shown on the field.)
- [x] Display pending, failed, and successfully posted states. (Busy button while posting; error text on failure; "Posted." or the points earned on success.)
- [ ] Provide report and soft-delete controls only when permitted. (Edit and delete shown only on your own comments. Reporting is Phase 9.)
- [x] Do not imply points were earned unless the API confirms eligibility/award behavior. (Points text comes only from `points_awarded`.)
- [ ] Add tests for short comments, self-owned logs, retries, and deleted comments. (Short comment, reply, delete, and tombstones covered; retries rely on the `Idempotency-Key`, no test yet.)

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: `npm run typecheck`, `npm test` 75/75 (branch `phase-6-journal`).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
