# TODO — Comments mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Display comments and replies on album logs with pagination if needed.
- [ ] Allow adding replies and show validation errors before submission.
- [ ] Display pending, failed, and successfully posted states.
- [ ] Provide report and soft-delete controls only when permitted.
- [ ] Do not imply points were earned unless the API confirms eligibility/award behavior.
- [ ] Add tests for short comments, self-owned logs, retries, and deleted comments.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
