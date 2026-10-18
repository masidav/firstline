# TODO — Notification worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Consume notification jobs and validate that the user/device remains eligible.
- [ ] Check user preferences and opt-in status immediately before sending.
- [ ] Deduplicate notification jobs by event and recipient.
- [ ] Send through the selected push provider with safe retry behavior.
- [ ] Deactivate invalid device tokens and record delivery outcomes.
- [ ] Rate-limit noncritical notifications and avoid sending sensitive content in lock-screen text.
- [ ] Add tests for retries, deduplication, revoked tokens, and changed preferences.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
