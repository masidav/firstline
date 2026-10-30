# TODO — Comments mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

**Superseded (decided 2026-10-25): comments were replaced by emoji reactions.** There is no comments UI in the app;
fans react to shared photos, album logs, and prompt answers with `features/feed/ReactionBar.tsx`
(`PUT/DELETE /reactions/{type}/{id}/{reaction}`, no points). See `PROJECT_TODO.md` (Phase 6) and the comments API
checklist (`services/api/src/features/comments/TODO.md`), which stays unbuilt. The items below are kept for the record;
the states they asked for are covered on the reaction bar.

## Implementation checklist
- [ ] ~~Display comments and replies on album logs with pagination if needed.~~ Not wanted (reactions instead).
- [ ] ~~Allow adding replies and show validation errors before submission.~~ Not wanted.
- [x] Display pending, failed, and successfully posted states. (Reactions: a failed reaction leaves the counts as they
      were and can be tried again; taps while one is in flight are ignored; the API's answer replaces the counts.
      `ReactionBar.test.tsx`, 2026-10-30.)
- [ ] ~~Provide report and soft-delete controls only when permitted.~~ Posts are reported from the feed card (`Feed.test.tsx`:
      other fans' posts can be reported, your own can't); a reaction is taken back by tapping it again.
- [x] Do not imply points were earned unless the API confirms eligibility/award behavior. (Reactions earn no points and
      the bar says nothing about points.)
- [x] Add tests for short comments, self-owned logs, retries, and deleted comments. (No comments to test. The reaction
      bar's retry, in-flight, own-reaction (the picker leaves out what you already gave), and unknown-key (a reaction
      this build doesn't know is skipped) cases are tested instead: `ReactionBar.test.tsx` 5 tests, 2026-10-30.)

## Verification
- [x] Add or update automated tests for the behavior above. (`ReactionBar.test.tsx` 5.)
- [x] Run the relevant tests and record actual results. 2026-10-30: `npm test` 51 suites, 327 tests passed.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
